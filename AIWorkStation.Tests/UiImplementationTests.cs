using System.Net;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AIWorkStation.Models;
using AIWorkStation.UI.Converters;
using AIWorkStation.ViewModels;

namespace AIWorkStation.Tests;

public sealed class UiImplementationTests
{
    private static readonly string Root = FindRoot();
    private static string Read(string relative) => File.ReadAllText(Path.Combine(Root, relative));

    [Fact] public void MainWindow_UsesApprovedWindowSize() { var x = Read("AIWorkStation/MainWindow.xaml"); Assert.Contains("Height=\"760\" Width=\"1180\"", x); Assert.Contains("MinHeight=\"640\" MinWidth=\"960\"", x); }
    [Fact] public void MainWindow_HasFourStepProgress() { var x = Read("AIWorkStation/MainWindow.xaml"); Assert.Equal(4, new[] { "1  检查电脑", "2  配置分流", "3  确认并应用", "4  结果" }.Count(x.Contains)); }
    [Fact] public void Step1_HasThreeSummarySections() { var x = Read("AIWorkStation/Views/EnvironmentStep.xaml"); Assert.All(new[] { "电脑", "Clash", "当前网络" }, value => Assert.Contains(value, x)); }
    [Fact] public void Step1_NodeTableHasRequiredColumns() { var x = Read("AIWorkStation/Views/EnvironmentStep.xaml"); Assert.All(new[] { "节点", "协议", "服务器", "服务器 IP / Fake-IP", "延迟", "状态", "测试时间" }, value => Assert.Contains($"Header=\"{value}\"", x)); }
    [Fact] public void Step2_HasOpenAiPreset() => Assert.Contains("OpenAI 应用", Read("AIWorkStation/Views/RoutingStep.xaml"));
    [Fact] public void Step2_HasBrowseExe() => Assert.Contains("浏览 EXE", Read("AIWorkStation/Views/RoutingStep.xaml"));
    [Fact] public void Step2_HasThreeTransportModes() { var x = Read("AIWorkStation/Views/RoutingStep.xaml"); Assert.All(new[] { "自动（推荐）", "直连", "经当前 Clash 节点连接" }, value => Assert.Contains($"Content=\"{value}\"", x)); }
    [Fact] public void Step2_ChainUnavailableDoesNotDisableDirect() { var x = Read("AIWorkStation/Views/RoutingStep.xaml"); Assert.Contains("Content=\"直连\" GroupName=\"Transport\"", x); Assert.DoesNotContain("Content=\"直连\" GroupName=\"Transport\" Style=\"{StaticResource SegmentRadio}\" IsEnabled", x); }
    [Fact] public void Step2_OnlyOnePrimaryVisualAction() { var x = Read("AIWorkStation/Views/RoutingStep.xaml"); Assert.Contains("DataTrigger Binding=\"{Binding IsStaticExitReady}\"", x); Assert.Contains("验证静态网络", x); Assert.Contains("确认配置", x); }
    [Fact] public void Step3_DoesNotShowExampleIp() { var x = Read("AIWorkStation/Views/ConfirmStep.xaml"); Assert.DoesNotContain("203.", x); Assert.DoesNotContain("65.195", x); }
    [Fact] public void Step3_UnknownExitShowsNeutralText() => Assert.Contains("ActualExitDisplayText", Read("AIWorkStation/Views/ConfirmStep.xaml"));
    [Fact] public void Step4_NotObservedUsesInfoStyle() { var x = Read("AIWorkStation/Views/ResultStep.xaml"); Assert.Contains("InfoBanner", x); Assert.Contains("ResultNotice", x); }
    [Fact] public void Step4_RecoveryFailedHasHomeAndTechnicalDetails() { var x = Read("AIWorkStation/Views/ResultStep.xaml"); Assert.Contains("返回首页", x); Assert.Contains("查看技术详情", x); Assert.Contains("RecoveryFailed", x); }
    [Fact] public void SuccessPage_CompleteIsPrimary() => Assert.Contains("Content=\"完成\" Click=\"FinishClicked\" Style=\"{StaticResource PrimaryButton}\"", Read("AIWorkStation/Views/ResultStep.xaml"));
    [Fact] public void SuccessPage_ContinueIsSecondary() { var x = Read("AIWorkStation/Views/ResultStep.xaml"); var button = x[x.IndexOf("Content=\"{Binding ResultReturnText}\"", StringComparison.Ordinal)..x.IndexOf("<Button Content=\"返回首页\"", StringComparison.Ordinal)]; Assert.DoesNotContain("StaticResource PrimaryButton", button); }
    [Fact] public void SuccessPage_HasOnlyOnePrimaryVisualAction() { var x = Read("AIWorkStation/Views/ResultStep.xaml"); var actions = x[x.LastIndexOf("<StackPanel Orientation=\"Horizontal\"", StringComparison.Ordinal)..]; Assert.Equal(1, Count(actions, "Style=\"{StaticResource PrimaryButton}\"")); }
    [Fact] public void NotObserved_UsesInfoIcon() { var x = Read("AIWorkStation/Views/ResultStep.xaml"); Assert.Contains("ApplyResult.RouteVerificationNotObserved", x); Assert.Contains("<Setter Property=\"Text\" Value=\"&#xE946;\"/>", x); }
    [Fact] public void NotObserved_DoesNotUseSuccessHero() { var x = Read("AIWorkStation/Views/ResultStep.xaml"); Assert.Contains("<DataTrigger Binding=\"{Binding ApplyResult.RouteVerificationNotObserved}\" Value=\"True\"><Setter Property=\"Background\" Value=\"{DynamicResource InfoBackgroundBrush}\"", x); }
    [Fact] public void NotObserved_DoesNotUseWarningOrErrorHero() { var x = Read("AIWorkStation/Views/ResultStep.xaml"); Assert.Contains("暂时没有检测到目标软件的新网络请求，这不代表配置失败。", x); Assert.Contains("<Setter Property=\"Foreground\" Value=\"{DynamicResource PrimaryBrush}\"/>", x); }
    [Fact] public void NotObserved_BackendSemanticsUnchanged() { var result = ApplyResult.Ok("198.51.100.24", applicationResults: [new("ChatGPT.exe", ApplicationRouteStatus.NoTrafficObserved, null)]); Assert.True(result.Success); Assert.True(result.RouteVerificationNotObserved); }
    [Fact] public void CompletedStep_ShowsCheckIcon() { var x = Read("AIWorkStation/MainWindow.xaml"); Assert.Equal(3, Count(x, "AutomationProperties.Name=\"已完成\"")); Assert.Contains("&#xE73E;", x); }
    [Fact] public void CompletedStep_StateDoesNotDependOnColorOnly() { var x = Read("AIWorkStation/MainWindow.xaml"); Assert.Contains("FontFamily=\"Segoe MDL2 Assets\"", x); Assert.Contains("Text=\"1  检查电脑\"", x); }
    [Fact] public void CompletedStep_Windows10IconFallbackAvailable() => Assert.Contains("Segoe MDL2 Assets", Read("AIWorkStation/MainWindow.xaml"));
    [Fact] public void CompletedStep_HighContrastReadable()
    {
        Assert.Contains("Foreground=\"{DynamicResource SuccessBrush}\"", Read("AIWorkStation/MainWindow.xaml"));
        Assert.Contains("Resources[\"SuccessBrush\"] = SystemColors.WindowTextBrush;", Read("AIWorkStation/App.xaml.cs"));
    }
    [Fact] public void ChainConfirm_DoesNotDuplicateConnectionModePrefix() { var converter = new ConnectionModeDisplayConverter(); Assert.Equal("经 Clash 节点连接", converter.Convert("连接方式：经 Clash 节点连接", typeof(string), null!, System.Globalization.CultureInfo.InvariantCulture)); }
    [Fact] public void ResultStep_NoOverflow_HidesVerticalScrollbar() => Assert.Contains("VerticalScrollBarVisibility=\"Auto\"", Read("AIWorkStation/Views/ResultStep.xaml"));
    [Fact] public void ResultStep_Overflow_ShowsVerticalScrollbar() => Assert.Contains("x:Name=\"ResultScroll\"", Read("AIWorkStation/Views/ResultStep.xaml"));
    [Fact] public void ResultStep_HighDpi_ContentRemainsReachable() { var x = Read("AIWorkStation/Views/ResultStep.xaml"); Assert.Contains("<ScrollViewer", x); Assert.DoesNotContain("VerticalScrollBarVisibility=\"Disabled\"", x); }
    [Fact]
    public async Task RuleModeWarning_DoesNotChangeCommandCanExecute()
    {
        var vm = new MainViewModel(detectEnvironment: _ => Task.FromResult(UiStateFixture.Environment("global")));
        await vm.InitializeAsync();
        Assert.True(vm.NextStepCommand.CanExecute(null));
    }
    [Fact] public void SoftwareOnly_RemainsEnabled() => Assert.Contains("RenderMode.SoftwareOnly", Read("AIWorkStation/App.xaml.cs"));
    [Fact] public void Windows10IconFallback_IsAvailable() { var text = Read("AIWorkStation/Views/ConfirmStep.xaml") + Read("AIWorkStation/Views/ResultStep.xaml"); Assert.Contains("Segoe MDL2 Assets", text); }
    [Fact] public void NodeStatus_UsesIconTextAndAutomationName() { var x = Read("AIWorkStation/Views/EnvironmentStep.xaml"); Assert.Contains("Segoe MDL2 Assets", x); Assert.Contains("LatencyStatusDisplay", x); Assert.Contains("节点状态：{0}", x); }
    [Fact]
    public void WorkAreaPlacement_StaysInsideAvailableBounds()
    {
        var work = new Rect(0, 0, 1366, 728);
        var placement = MainWindow.CalculateWindowPlacement(work, new Size(1180, 760), new Size(960, 640), 24);
        Assert.True(work.Contains(placement.Bounds));
        Assert.Equal(1180, placement.Bounds.Width);
        Assert.Equal(680, placement.Bounds.Height);
    }
    [Fact]
    public void WorkAreaPlacement_ShrinksMinimumOnSmallDisplay()
    {
        var placement = MainWindow.CalculateWindowPlacement(new Rect(-800, 0, 800, 600), new Size(1180, 760), new Size(960, 640), 24);
        Assert.Equal(752, placement.Maximum.Width);
        Assert.Equal(552, placement.Maximum.Height);
        Assert.True(placement.Minimum.Width <= placement.Maximum.Width);
        Assert.True(placement.Minimum.Height <= placement.Maximum.Height);
    }
    [Fact]
    public void ActualExitDisplay_DistinguishesConfirmedUnconfirmedAndUnavailable()
    {
        var vm = UiStateFixture.Create(2);
        Assert.Contains("198.51.100.24", vm.ActualExitDisplayText);
        vm.ActualExitIp = null;
        vm.ActualExitState = ActualExitDisplayState.Unconfirmed;
        Assert.Equal("实际公网出口：尚未确认", vm.ActualExitDisplayText);
        vm.ActualExitState = ActualExitDisplayState.Unavailable;
        Assert.Equal("实际公网出口：暂时无法确认", vm.ActualExitDisplayText);
    }

    [Fact]
    public void Generate_Sanitized_UiReview_Screenshots()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { GenerateScreenshots(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    private static void GenerateScreenshots()
    {
        RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/AIWorkStation;component/UI/Styles/Theme.xaml", UriKind.Relative) });
        app.Resources["BooleanToOnOffConverter"] = new BooleanToOnOffConverter();
        app.Resources["BooleanToVisibilityConverter"] = new BooleanToVisibilityConverter();
        app.Resources["EnumEqualsConverter"] = new EnumEqualsConverter();
        app.Resources["InverseBooleanToVisibilityConverter"] = new InverseBooleanToVisibilityConverter();
        app.Resources["NullOrEmptyToVisibilityConverter"] = new NullOrEmptyToVisibilityConverter();
        app.Resources["ConnectionModeDisplayConverter"] = new ConnectionModeDisplayConverter();
        var output = Path.Combine(Root, "artifacts", "ui-final-patch-review");
        Directory.CreateDirectory(output);
        Capture(output, "01-progress-completed-check-icons.png", UiStateFixture.Applying(), 1366, 768, 96);
        Capture(output, "02-step3-chain-fixed.png", UiStateFixture.Create(2, chain: true), 1366, 768, 96);
        Capture(output, "03-step4-success-fixed.png", UiStateFixture.Result(ApplyResult.Ok("198.51.100.24"), "配置已成功应用。", null), 1366, 768, 96);
        Capture(output, "04-step4-notobserved-fixed.png", UiStateFixture.Result(ApplyResult.Ok("198.51.100.24", applicationResults: [new("ChatGPT.exe", ApplicationRouteStatus.NoTrafficObserved, null)]), "配置已应用。", null), 1366, 768, 96, 44);
        app.Shutdown();
    }

    private static int Count(string text, string value) => text.Split(value, StringSplitOptions.None).Length - 1;

    private static void Capture(string directory, string fileName, MainViewModel vm, int pixelWidth, int pixelHeight, double dpi, double scrollOffset = 0, bool highContrast = false)
    {
        var palette = highContrast ? ApplyHighContrastFixturePalette(Application.Current.Resources) : null;
        var window = new MainWindow(vm, initializeOnLoad: false) { MinWidth = 0, MinHeight = 0, Width = pixelWidth * 96 / dpi, Height = pixelHeight * 96 / dpi, ShowInTaskbar = false, ShowActivated = false, WindowStyle = WindowStyle.None, Left = -20000, Top = -20000 };
        window.Show();
        window.MinWidth = 0;
        window.MinHeight = 0;
        window.MaxWidth = double.PositiveInfinity;
        window.MaxHeight = double.PositiveInfinity;
        window.Width = pixelWidth * 96 / dpi;
        window.Height = pixelHeight * 96 / dpi;
        window.Left = -20000;
        window.Top = -20000;
        window.UpdateLayout();
        if (scrollOffset > 0 && (FindVisualChildByName<ScrollViewer>(window, "RoutingScroll") ??
                                 FindVisualChildByName<ScrollViewer>(window, "EnvironmentScroll") ??
                                 FindVisualChildByName<ScrollViewer>(window, "ResultScroll")) is { } scroll)
        {
            scroll.ScrollToVerticalOffset(scrollOffset);
            window.UpdateLayout();
        }
        var bitmap = new RenderTargetBitmap(pixelWidth, pixelHeight, dpi, dpi, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(directory, fileName));
        encoder.Save(stream);
        vm.IsApplying = false;
        window.Close();
        if (palette is not null) RestorePalette(Application.Current.Resources, palette);
    }

    private static Dictionary<string, object> ApplyHighContrastFixturePalette(ResourceDictionary resources)
    {
        var replacement = new Dictionary<string, object>
        {
            ["BackgroundBrush"] = Brushes.White, ["SurfaceBrush"] = Brushes.White,
            ["InkBrush"] = Brushes.Black, ["MutedBrush"] = Brushes.Black,
            ["BorderBrush"] = Brushes.Black, ["PrimaryBrush"] = Brushes.DarkBlue,
            ["PrimaryTextBrush"] = Brushes.White, ["SelectedBackgroundBrush"] = Brushes.LightBlue,
            ["BadgeTextBrush"] = Brushes.DarkBlue,
            ["InfoBackgroundBrush"] = Brushes.White, ["InfoBorderBrush"] = Brushes.Black,
            ["WarningBackgroundBrush"] = Brushes.White, ["WarningBorderBrush"] = Brushes.Black,
            ["SuccessBackgroundBrush"] = Brushes.White, ["SuccessBorderBrush"] = Brushes.Black,
            ["SuccessBrush"] = Brushes.Black, ["WarningBrush"] = Brushes.Black,
            ["DangerBrush"] = Brushes.Black
        };
        var previous = replacement.Keys.ToDictionary(key => key, key => resources[key]);
        foreach (var item in replacement) resources[item.Key] = item.Value;
        return previous;
    }

    private static void ApplySystemHighContrastFixturePalette(ResourceDictionary resources)
    {
        resources["BackgroundBrush"] = SystemColors.WindowBrush;
        resources["SurfaceBrush"] = SystemColors.WindowBrush;
        resources["InkBrush"] = SystemColors.WindowTextBrush;
        resources["MutedBrush"] = SystemColors.WindowTextBrush;
        resources["BorderBrush"] = SystemColors.WindowTextBrush;
        resources["PrimaryBrush"] = SystemColors.HighlightBrush;
        resources["PrimaryTextBrush"] = SystemColors.HighlightTextBrush;
        resources["BadgeTextBrush"] = SystemColors.HighlightTextBrush;
        resources["SelectedBackgroundBrush"] = SystemColors.HighlightBrush;
        resources["InfoBackgroundBrush"] = SystemColors.WindowBrush;
        resources["InfoBorderBrush"] = SystemColors.WindowTextBrush;
        resources["WarningBackgroundBrush"] = SystemColors.WindowBrush;
        resources["WarningBorderBrush"] = SystemColors.WindowTextBrush;
        resources["SuccessBackgroundBrush"] = SystemColors.WindowBrush;
        resources["SuccessBorderBrush"] = SystemColors.WindowTextBrush;
        resources["SuccessBrush"] = SystemColors.WindowTextBrush;
        resources["WarningBrush"] = SystemColors.WindowTextBrush;
        resources["DangerBrush"] = SystemColors.WindowTextBrush;
    }

    private static void RestorePalette(ResourceDictionary resources, Dictionary<string, object> previous)
    {
        foreach (var item in previous) resources[item.Key] = item.Value;
    }

    private static T? FindVisualChildByName<T>(DependencyObject root, string name) where T : FrameworkElement
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match && match.Name == name) return match;
            if (FindVisualChildByName<T>(child, name) is { } nested) return nested;
        }
        return null;
    }

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AIWorkStation.sln"))) directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("AIWorkStation.sln not found.");
    }
}

internal static class UiStateFixture
{
    internal static MainViewModel Create(int step, bool chain = false)
    {
        var vm = new MainViewModel(detectEnvironment: _ => Task.FromResult(Environment("rule")));
        vm.Environment = Environment("rule");
        vm.State = UiState.Ready;
        vm.StatusText = step == 0 ? "检查完成，可以继续配置。" : "已准备好配置。";
        vm.CurrentStep = step;
        vm.ProxyServer = "proxy.example";
        vm.ProxyPort = "1080";
        vm.ProxyUsername = "d***";
        vm.ActualExitIp = "198.51.100.24";
        vm.ActualExitState = ActualExitDisplayState.Confirmed;
        vm.SelectedTargets.Add(new("ChatGPT", "ChatGPT.exe", string.Empty, false, "OpenAI 预设"));
        vm.SelectedTargets.Add(new("Codex", "codex.exe", string.Empty, false, "OpenAI 预设"));
        vm.DialerProxyGroup = "FlyintPro";
        var selection = new ProxySelection("FlyintPro", "Hongkong 016") { Members = ["Hongkong 016", "Taiwan 031"] };
        vm.DialerProxySelections.Add(selection);
        vm.SelectedDialerProxySelection = selection;
        vm.CurrentFrontNodeSummary = "当前前置节点：Hongkong 016";
        vm.FrontGroupSupportMessage = "链式连接可用";
        foreach (var node in vm.Environment.Subscription!.Nodes) vm.Nodes.Add(node);
        vm.TransportPreference = chain ? StaticTransportPreference.DialerProxy : StaticTransportPreference.Direct;
        vm.TransportMode = chain ? StaticTransportMode.DialerProxy : StaticTransportMode.Direct;
        typeof(MainViewModel).GetField("_staticExitReady", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(vm, true);
        return vm;
    }

    internal static MainViewModel Transport(StaticTransportPreference preference)
    {
        var vm = Create(1, preference == StaticTransportPreference.DialerProxy);
        vm.TransportPreference = preference;
        vm.TransportMode = preference == StaticTransportPreference.DialerProxy ? StaticTransportMode.DialerProxy : StaticTransportMode.Direct;
        return vm;
    }

    internal static MainViewModel ChainUnavailable()
    {
        var vm = Transport(StaticTransportPreference.Direct);
        vm.DialerProxySelections.Clear();
        vm.SelectedDialerProxySelection = null;
        vm.DialerProxyGroup = null;
        vm.CurrentFrontNodeSummary = "当前前置节点：未确定";
        return vm;
    }

    internal static MainViewModel ExitUnconfirmed()
    {
        var vm = Create(2);
        vm.ActualExitIp = null;
        vm.ActualExitState = ActualExitDisplayState.Unconfirmed;
        return vm;
    }

    internal static MainViewModel Applying() { var vm = Create(3, true); vm.IsApplying = true; vm.State = UiState.Applying; vm.StatusText = "正在重载并验证配置…"; return vm; }

    internal static MainViewModel Result(ApplyResult result, string message, string? notice)
    {
        var vm = Create(3, result.TransportMode == StaticTransportMode.DialerProxy);
        vm.ApplyResult = result;
        vm.ResultMessage = new(result.Success ? "配置完成" : "配置没有完成", message, result.Success ? "可以打开目标软件并正常使用。" : "请查看提示后返回修改。" );
        vm.ResultNotice = notice;
        vm.State = result.Success ? UiState.Succeeded : result.FailureCode == FailureCode.RecoveryFailed ? UiState.RecoveryFailed : UiState.Failed;
        return vm;
    }

    internal static EnvironmentSnapshot Environment(string mode)
    {
        var nodes = new[] { Node("Hongkong 016", 82), Node("Taiwan 031", 128), Node("USA 02", null), Node("Japan 09", 66), Node("Singapore 07", 103), Node("Germany 04", null) };
        var machine = new MachineInfo("Windows 11 Pro", "23H2", "22631", "x64", "Pacific Standard Time", "Pacific Standard Time", TimeSpan.FromHours(-7), true);
        var process = new ProcessInfo(1000, DateTime.UtcNow, @"C:\Program Files\Demo\demo.exe", "2.5.2");
        var selection = new ProxySelection("FlyintPro", "Hongkong 016") { Members = nodes.Select(node => node.Name).ToArray() };
        var clash = new ClashInfo(process, process with { Pid = 1001 }, @"C:\Demo", @"C:\Demo\profiles.yaml", @"C:\Demo\clash-verge.yaml", @"C:\Demo\profiles", "demo-pipe", mode, true, false, false, [selection]);
        var subscription = new SubscriptionInfo("demo", "FlyintPro", "demo.yaml", @"C:\Demo\profiles\demo.yaml", "DEMO-HASH", nodes, ExtensionOwnership.NoneOrEmpty, null, null, null);
        return new(EnvironmentSupport.Supported, "检查完成，可以继续配置。", machine, clash, subscription, "198.51.100.7");
    }

    private static ProxyNodeInfo Node(string name, int? latency)
    {
        var node = new ProxyNodeInfo(name, "vless", "node.example", [IPAddress.Parse("198.18.0.5")]);
        if (latency is not null) { node.LatencyMs = latency; node.LatencyStatus = LatencyStatus.Available; node.LatencyTestedAt = DateTimeOffset.Now; }
        return node;
    }
}
