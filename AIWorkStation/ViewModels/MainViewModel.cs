using System.Collections.ObjectModel;
using AIWorkStation.Models;
using AIWorkStation.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;

namespace AIWorkStation.ViewModels;

public enum ActualExitDisplayState { Unconfirmed, Confirmed, Unavailable }

public partial class MainViewModel : ObservableObject
{
    private readonly Func<CancellationToken, Task<EnvironmentSnapshot>> _detectEnvironment;
    private readonly ApplicationFinder _applicationFinder = new();
    private readonly Func<StaticExitConfig, CancellationToken, Task<StaticExitTestResult>> _testStaticExit;
    private readonly Func<ApplyContext, IProgress<string>?, CancellationToken, Task<ApplyResult>> _apply;
    private readonly UserMessageMapper _messageMapper = new();
    private readonly TemporaryCredentialCache _credentialCache;
    private readonly Func<string, NodeLatencyTester> _latencyTesterFactory;
    private readonly Func<string?> _selectExecutable;
    private readonly Func<string, ApplicationTarget> _createManualApplication;
    private CancellationTokenSource? _latencyCancellation;
    private bool _suppressProxyInputReset;
    private int _proxyInputRevision;

    public MainViewModel(
        Func<CancellationToken, Task<EnvironmentSnapshot>>? detectEnvironment = null,
        Func<StaticExitConfig, CancellationToken, Task<StaticExitTestResult>>? testStaticExit = null,
        Func<ApplyContext, IProgress<string>?, CancellationToken, Task<ApplyResult>>? apply = null,
        TemporaryCredentialCache? credentialCache = null,
        Func<string, NodeLatencyTester>? latencyTesterFactory = null,
        Func<string?>? selectExecutable = null,
        Func<string, ApplicationTarget>? createManualApplication = null)
    {
        var environmentDetector = new EnvironmentDetector();
        var staticExitTester = new StaticExitTester();
        var applyEngine = new ApplyEngine();
        _detectEnvironment = detectEnvironment ?? environmentDetector.DetectAsync;
        _testStaticExit = testStaticExit ?? staticExitTester.TestAsync;
        _apply = apply ?? applyEngine.ApplyAsync;
        _credentialCache = credentialCache ?? new TemporaryCredentialCache();
        _latencyTesterFactory = latencyTesterFactory ??
            (pipe => new NodeLatencyTester(new MihomoNamedPipeClient(pipe)));
        _selectExecutable = selectExecutable ?? SelectExecutable;
        _createManualApplication = createManualApplication ?? ApplicationFinder.FromManualExecutable;
    }

    public IReadOnlyList<string> StepTitles { get; } = ["1  检查电脑", "2  配置分流", "3  确认并应用", "4  结果"];
    public IReadOnlyList<StaticProxyProtocol> ProxyProtocols { get; } = Enum.GetValues<StaticProxyProtocol>();
    public ObservableCollection<ProxyNodeInfo> Nodes { get; } = [];
    public ObservableCollection<ApplicationTarget> SearchResults { get; } = [];
    public ObservableCollection<ApplicationTarget> SelectedTargets { get; } = [];
    public ObservableCollection<ProxySelection> DialerProxySelections { get; } = [];

    [ObservableProperty] private int currentStep;
    [ObservableProperty] private UiState state = UiState.Checking;
    [ObservableProperty] private EnvironmentSnapshot? environment;
    [ObservableProperty] private string statusText = "正在检查电脑…";
    [ObservableProperty] private string searchQuery = string.Empty;
    [ObservableProperty] private ApplicationTarget? selectedSearchResult;
    [ObservableProperty] private StaticProxyProtocol proxyProtocol = StaticProxyProtocol.Socks5;
    [ObservableProperty] private string proxyServer = string.Empty;
    [ObservableProperty] private string proxyPort = string.Empty;
    [ObservableProperty] private string proxyUsername = string.Empty;
    [ObservableProperty] private string proxyPassword = string.Empty;
    [ObservableProperty] private bool saveProxyTemporarily = true;
    [ObservableProperty] private bool isTestingLatency;
    [ObservableProperty] private string currentFrontNodeSummary = "当前前置节点：未确定";
    [ObservableProperty] private string frontGroupSupportMessage = "当前环境仅支持直连模式。";
    [ObservableProperty] private string? currentNodeLatencyNotice;
    [ObservableProperty] private string? actualExitIp;
    [ObservableProperty] private ActualExitDisplayState actualExitState = ActualExitDisplayState.Unconfirmed;
    [ObservableProperty] private StaticTransportMode transportMode = StaticTransportMode.Direct;
    [ObservableProperty] private StaticTransportPreference transportPreference = StaticTransportPreference.Auto;
    [ObservableProperty] private string? dialerProxyGroup;
    [ObservableProperty] private ProxySelection? selectedDialerProxySelection;
    [ObservableProperty] private ApplyResult? applyResult;
    [ObservableProperty] private UserMessage? resultMessage;
    [ObservableProperty] private string? resultNotice;
    [ObservableProperty] private bool isApplying;
    private bool _staticExitReady;

    public bool IsEnvironmentReady => State == UiState.Ready &&
                                      Environment?.Support == EnvironmentSupport.Supported;
    public bool IsStaticExitReady => _staticExitReady;
    public bool DialerProxyAvailable => !string.IsNullOrWhiteSpace(DialerProxyGroup);
    public string ConnectionModeSummary => TransportMode == StaticTransportMode.Direct
        ? "连接方式：直连"
        : "连接方式：经 Clash 节点连接";
    public string FrontGroupSummary => $"前置策略组：{DialerProxyGroup ?? "未确定"}";
    public string StaticExitSummary => !string.IsNullOrWhiteSpace(ActualExitIp)
        ? $"实际出口：{ActualExitIp}"
        : IsStaticExitReady && TransportMode == StaticTransportMode.DialerProxy
            ? "静态出口将在应用时经当前 Clash 节点验证"
            : "尚未验证";
    public string ActualExitDisplayText => ActualExitState switch
    {
        ActualExitDisplayState.Confirmed when !string.IsNullOrWhiteSpace(ActualExitIp) => $"实际公网出口：{ActualExitIp}",
        ActualExitDisplayState.Unavailable => "实际公网出口：暂时无法确认",
        _ => "实际公网出口：尚未确认"
    };
    public string TimeZoneSummary => Environment is null ? "—" : $"UTC{FormatOffset(Environment.Machine.UtcOffset)} · {Environment.Machine.TimeZoneId}";
    public string TargetSummary => SelectedTargets.Count == 0
        ? "尚未选择"
        : string.Join(" + ", SelectedTargets
            .Select(target => $"{target.DisplayName} ({target.ExecutableName})")
            .Distinct(StringComparer.CurrentCultureIgnoreCase));
    public string RecoverySummary => ApplyResult?.RecoveryAttempted == true ? (ApplyResult.RecoverySucceeded ? "原配置已恢复" : "无法确认恢复") : "未触发恢复";
    public string ResultPageTitle => ApplyResult switch
    {
        { Success: true, NoChangesRequired: true } => "当前配置已经是最新状态",
        { Success: true } => "配置完成",
        { FailureCode: FailureCode.RecoveryFailed } => "当前网络配置需要检查",
        { FilesModified: true, RecoverySucceeded: true } => "配置没有完成",
        _ => "没有进行修改"
    };
    public bool CanReturnToRouting => ApplyResult is not null && ApplyResult.FailureCode != FailureCode.RecoveryFailed;
    public string ResultReturnText => ApplyResult?.Success == true
        ? "继续配置其他软件"
        : ApplyResult?.RecoverySucceeded == true ? "返回配置" : "返回修改";
    public bool IsSuccessResult => ApplyResult?.Success == true;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        CancelLatencyTests(detach: true);
        ApplyResult = null;
        ResultMessage = null;
        ResultNotice = null;
        State = UiState.Checking;
        Environment = null;
        Nodes.Clear();
        StatusText = "正在读取 Windows、Clash Verge、订阅和当前网络状态…";
        Environment = await _detectEnvironment(cancellationToken);
        DialerProxySelections.Clear();
        if (Environment.Clash is not null && Environment.Subscription is not null)
            foreach (var selection in ClashVergeDetector.FindSafeDialerProxySelections(
                         Environment.Clash, Environment.Subscription.Name))
                DialerProxySelections.Add(selection);
        SelectedDialerProxySelection = DialerProxySelections.FirstOrDefault();
        var frontSelection = SelectedDialerProxySelection;
        if (frontSelection is null)
        {
            DialerProxyGroup = null;
            CurrentFrontNodeSummary = "当前前置节点：未确定";
            FrontGroupSupportMessage = "当前没有可用于链式连接的 Clash 策略组。";
        }
        CurrentNodeLatencyNotice = null;
        _staticExitReady = false;
        ActualExitIp = null;
        ActualExitState = ActualExitDisplayState.Unconfirmed;
        TransportPreference = StaticTransportPreference.Auto;
        TransportMode = RuntimeTransportMode(Environment.Clash);
        if (Environment.Subscription is not null)
        {
            foreach (var node in Environment.Subscription.Nodes
                         .OrderByDescending(node => frontSelection is not null &&
                                                    node.Name.Equals(frontSelection.CurrentSelection, StringComparison.Ordinal)))
                Nodes.Add(node);
        }
        State = Environment.Support == EnvironmentSupport.Supported ? UiState.Ready : UiState.Failed;
        StatusText = Environment.ReasonZh;
        OnPropertyChanged(nameof(IsEnvironmentReady));
        OnPropertyChanged(nameof(TimeZoneSummary));
        NextStepCommand.NotifyCanExecuteChanged();
        TestAllNodesCommand.NotifyCanExecuteChanged();

        // 启动只自动观测当前真实前置节点，其余节点保持“未测试”，延迟永远不作为 Apply 门禁。
        if (IsEnvironmentReady && frontSelection is not null && Environment.Clash is not null)
        {
            if (Nodes.Any(node => node.Name.Equals(frontSelection.CurrentSelection, StringComparison.Ordinal)))
                await TestCurrentNodeLatencyAsync(Environment.Clash.ControllerPipe, frontSelection.CurrentSelection, cancellationToken);
            else
                CurrentNodeLatencyNotice = "当前前置选择暂不可测试（可能来自 Provider）。";
        }
    }

    [RelayCommand(CanExecute = nameof(IsEnvironmentReady))]
    private async Task NextStepAsync()
    {
        if (CurrentStep >= 2) return;
        CancelLatencyTests(detach: true);
        CurrentStep++;
        if (CurrentStep == 1)
        {
            await LoadCredentialCacheAsync();
            if (string.IsNullOrWhiteSpace(ProxyServer))
                StatusText = "请选择目标软件并配置静态出口。";
        }
    }

    [RelayCommand(CanExecute = nameof(CanGoPrevious))]
    private void PreviousStep()
    {
        if (CanGoPrevious()) CurrentStep--;
    }

    [RelayCommand]
    private async Task RecheckAsync() => await InitializeAsync();

    [RelayCommand(CanExecute = nameof(CanTestLatency))]
    private async Task TestAllNodesAsync()
    {
        if (Environment?.Clash is null || Nodes.Count == 0 || IsTestingLatency) return;
        var run = BeginLatencyRun();
        try
        {
            StatusText = "正在测试全部订阅节点延迟；这不会修改节点选择或影响正式应用。";
            var selected = ClashVergeDetector.FindSafeDialerProxySelection(
                Environment.Clash, Environment.Subscription?.Name ?? string.Empty)?.CurrentSelection;
            await _latencyTesterFactory(Environment.Clash.ControllerPipe)
                .TestAllAsync(Nodes.ToArray(), selected, run.Token);
            if (ReferenceEquals(_latencyCancellation, run))
                StatusText = "节点延迟测试完成。显示内容仅代表本次测试结果。";
        }
        catch (OperationCanceledException)
        {
            if (ReferenceEquals(_latencyCancellation, run))
                StatusText = "已停止节点延迟测试；未完成的节点保持未测试。";
        }
        finally
        {
            FinishLatencyRun(run);
        }
    }

    [RelayCommand(CanExecute = nameof(CanStopLatency))]
    private void StopLatencyTest() => CancelLatencyTests();

    [RelayCommand]
    private async Task SearchApplicationsAsync()
    {
        StatusText = "正在搜索 Windows 程序…";
        var results = await _applicationFinder.FindAsync(SearchQuery);
        SearchResults.Clear();
        foreach (var item in results) SearchResults.Add(item);
        StatusText = results.Count == 0 ? "没有找到匹配程序。" : $"找到 {results.Count} 个程序。";
    }

    [RelayCommand]
    private void BrowseExecutable()
    {
        var path = _selectExecutable();
        if (string.IsNullOrWhiteSpace(path)) return;

        ApplicationTarget target;
        try { target = _createManualApplication(path); }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or
                                   ArgumentException or NotSupportedException)
        {
            StatusText = "无法找到所选程序，请重新选择。";
            return;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            StatusText = "无法读取所选程序，请选择其他程序。";
            return;
        }

        if (SelectedTargets.Any(item =>
                item.ExecutableName.Equals(target.ExecutableName, StringComparison.OrdinalIgnoreCase)))
        {
            StatusText = "已选择同名程序；该规则会应用于所有同名进程。";
            return;
        }

        SelectedTargets.Add(target);
        StatusText = $"已添加 {target.ExecutableName}。同名程序将共用同一条进程分流规则。";
        OnPropertyChanged(nameof(TargetSummary));
    }

    [RelayCommand]
    private async Task SelectOpenAiPresetAsync()
    {
        var chatGpt = await _applicationFinder.FindAsync("ChatGPT");
        var codex = await _applicationFinder.FindAsync("codex");
        var matched = new OpenAIApplicationMatcher().CreatePresetTargets(chatGpt.Concat(codex));
        var duplicateExecutable = matched.GroupBy(app => app.ExecutableName, StringComparer.OrdinalIgnoreCase)
            .Any(group => group.Select(app => app.ExecutablePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1) ||
            matched.Any(app => SelectedTargets.Any(target =>
                target.ExecutableName.Equals(app.ExecutableName, StringComparison.OrdinalIgnoreCase) &&
                !target.ExecutablePath.Equals(app.ExecutablePath, StringComparison.OrdinalIgnoreCase)));
        foreach (var app in matched)
            if (!SelectedTargets.Any(target => target.ExecutableName.Equals(app.ExecutableName, StringComparison.OrdinalIgnoreCase))) SelectedTargets.Add(app);
        StatusText = "OpenAI 应用将共用一个 AI静态链。";
        if (duplicateExecutable) StatusText += " 该规则会应用于所有同名进程。";
        OnPropertyChanged(nameof(TargetSummary));
    }

    [RelayCommand]
    private void AddSelectedApplication()
    {
        if (SelectedSearchResult is null) return;
        if (SelectedTargets.Any(target => target.ExecutableName.Equals(SelectedSearchResult.ExecutableName, StringComparison.OrdinalIgnoreCase)))
        {
            StatusText = "已选择同名程序；该规则会应用于所有同名进程。";
            return;
        }
        SelectedTargets.Add(SelectedSearchResult);
        OnPropertyChanged(nameof(TargetSummary));
    }

    private static string? SelectExecutable()
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择需要分流的程序",
            Filter = "Windows 程序 (*.exe)|*.exe",
            CheckFileExists = true,
            Multiselect = false
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    [RelayCommand]
    private void RemoveApplication(ApplicationTarget? target)
    {
        if (target is not null) SelectedTargets.Remove(target);
        OnPropertyChanged(nameof(TargetSummary));
    }

    [RelayCommand]
    private async Task TestStaticExitAsync()
    {
        ApplyResult = null;
        ResultMessage = null;
        ActualExitIp = null;
        ActualExitState = ActualExitDisplayState.Unconfirmed;
        _staticExitReady = false;
        if (!int.TryParse(ProxyPort, out var port)) { StatusText = "请输入有效的代理端口。"; return; }
        var config = CreateStaticExit(port);
        var inputRevision = _proxyInputRevision;
        var cacheWarning = await SaveCredentialCacheAsync(config);
        if (inputRevision != _proxyInputRevision)
        {
            StatusText = "代理信息已变更，请重新验证。";
            if (cacheWarning is not null) StatusText += " " + cacheWarning;
            return;
        }
        StatusText = "正在通过静态代理验证连接、认证和实际出口 IP…";
        var result = await _testStaticExit(config, CancellationToken.None);
        if (inputRevision != _proxyInputRevision)
        {
            StatusText = "代理信息已变更，请重新验证。";
            if (cacheWarning is not null) StatusText += " " + cacheWarning;
            return;
        }
        if (result.Success)
        {
            TransportMode = TransportPreference == StaticTransportPreference.DialerProxy
                ? StaticTransportMode.DialerProxy
                : StaticTransportMode.Direct;
            ActualExitIp = result.ActualExitIp;
            ActualExitState = string.IsNullOrWhiteSpace(result.ActualExitIp)
                ? ActualExitDisplayState.Unavailable
                : ActualExitDisplayState.Confirmed;
            _staticExitReady = true;
            StatusText = TransportMode == StaticTransportMode.DialerProxy
                ? $"直连验证可用；已固定经 Clash 节点连接，实际出口：{ActualExitIp}"
                : $"直连可用，实际出口：{ActualExitIp}";
            if (result.SanitizedDetail?.Contains("警告", StringComparison.Ordinal) == true)
                StatusText += $" · {result.SanitizedDetail}";
        }
        else if (result.FailureCode is FailureCode.StaticProxyConnectionFailed or FailureCode.StaticProxyTimeout &&
                 DialerProxyAvailable && TransportPreference != StaticTransportPreference.Direct)
        {
            // 直连网络级失败时才启用链式路径；本次 Apply 期间固定选择，不做后台抖动切换。
            TransportMode = StaticTransportMode.DialerProxy;
            _staticExitReady = true;
            StatusText = "直连不可用，将在应用时经当前 Clash 节点验证静态出口。";
        }
        else
        {
            if (result.FailureCode == FailureCode.ExitIpLookupFailed)
                ActualExitState = ActualExitDisplayState.Unavailable;
            StatusText = _messageMapper.Map(result.FailureCode).MessageZh;
            if (result.FailureCode == FailureCode.StaticProxyAuthenticationFailed)
            {
                await ClearCachedPasswordWithoutBlockingAsync();
                ClearPlaintextPassword();
                StatusText += " 已清除临时缓存中的密码，请重新输入。";
            }
        }
        if (cacheWarning is not null) StatusText += " " + cacheWarning;
        OnPropertyChanged(nameof(IsStaticExitReady));
        OnPropertyChanged(nameof(ConnectionModeSummary));
        OnPropertyChanged(nameof(StaticExitSummary));
    }

    [RelayCommand]
    private void GoToConfirm()
    {
        if (SelectedTargets.Count == 0) { StatusText = "请至少选择一个目标软件。"; return; }
        if (!IsStaticExitReady) { StatusText = "请先验证静态出口。"; return; }
        CurrentStep = 2;
        OnPropertyChanged(nameof(TargetSummary));
    }

    [RelayCommand]
    private async Task ClearSavedInformationAsync()
    {
        var deleted = await _credentialCache.ClearAsync();
        _suppressProxyInputReset = true;
        try
        {
            ProxyProtocol = StaticProxyProtocol.Socks5;
            ProxyServer = string.Empty;
            ProxyPort = string.Empty;
            ProxyUsername = string.Empty;
            ProxyPassword = string.Empty;
        }
        finally { _suppressProxyInputReset = false; }
        InvalidateStaticExitValidation();
        StatusText = deleted
            ? "已清除本机临时保存的代理信息。"
            : "无法删除本机临时保存的信息；输入框已清空，请关闭占用文件的程序后重试。";
    }

    [RelayCommand(CanExecute = nameof(CanReturnToRouting))]
    private async Task ReturnToRoutingAsync()
    {
        ClearPlaintextPassword();
        var environmentRefreshed = false;
        if (ApplyResult?.Success == true)
        {
            // 成功后的继续配置必须重新取得 Profile/Script hash、Selector 与公网 IP，不能复用旧快照。
            CurrentStep = 0;
            await InitializeAsync();
            if (!IsEnvironmentReady) return;
            environmentRefreshed = true;
        }
        CurrentStep = 1;
        await LoadCredentialCacheAsync();
        StatusText = environmentRefreshed
            ? "环境已刷新，请选择其他目标软件并重新验证静态出口。"
            : "请修改目标软件或静态出口后重新验证。";
    }

    [RelayCommand]
    private async Task ReturnHomeAsync()
    {
        ClearPlaintextPassword();
        CurrentStep = 0;
        await InitializeAsync();
    }

    [RelayCommand(CanExecute = nameof(CanApply))]
    private async Task ApplyAsync()
    {
        if (Environment?.Clash is null || Environment.Subscription is null || !IsStaticExitReady || !int.TryParse(ProxyPort, out var port)) return;
        IsApplying = true;
        ApplyCommand.NotifyCanExecuteChanged();
        try
        {
            State = UiState.Applying;
            StatusText = "开始正式应用…";
            var route = new RouteConfiguration(SelectedTargets.ToArray(), CreateStaticExit(port), ActualExitIp ?? string.Empty, Environment.Subscription.Name)
            {
                TransportMode = TransportMode,
                TransportPreference = TransportPreference,
                DialerProxyGroup = DialerProxyGroup
            };
            var context = new ApplyContext(Environment, route,
                new(Environment.Clash.ProfilesPath, Environment.Subscription.ProfilesHash),
                Environment.Subscription.ScriptPath is not null && Environment.Subscription.ScriptHash is not null
                    ? new(Environment.Subscription.ScriptPath, Environment.Subscription.ScriptHash) : null);
            var progress = new Progress<string>(message => StatusText = message);
            var cacheWarning = await SaveCredentialCacheAsync(route.StaticExit);
            ApplyResult = await _apply(context, progress, CancellationToken.None);
            ResultNotice = cacheWarning;
            ResultMessage = _messageMapper.Map(ApplyResult.FailureCode);
            var notObservedApplications = ApplyResult.ApplicationResults
                .Where(result => result.Status == ApplicationRouteStatus.NoTrafficObserved)
                .Select(result => result.ExecutableName)
                .ToArray();
            if (ApplyResult.RouteVerificationNotObserved)
            {
                ResultMessage = new UserMessage(
                    "配置已应用",
                    "暂时没有检测到 ChatGPT 的网络请求，因此还没有完成实际程序流量验证。",
                    "打开 ChatGPT 并正常使用后，AI WorkStation 可以在下次检查中确认实际分流状态。");
            }
            else if (ApplyResult.Success && notObservedApplications.Length > 0)
            {
                var routeNotice = $"暂时未检测到以下程序的网络请求：{string.Join("、", notObservedApplications)}。";
                ResultNotice = string.IsNullOrWhiteSpace(ResultNotice)
                    ? routeNotice
                    : ResultNotice + " " + routeNotice;
            }
            if (ApplyResult.Success) TransportMode = ApplyResult.TransportMode;
            if (ApplyResult.Success && ApplyResult.ActualExitIp is not null)
            {
                ActualExitIp = ApplyResult.ActualExitIp;
                ActualExitState = ActualExitDisplayState.Confirmed;
            }
            if (ApplyResult.NoChangesRequired)
                ResultMessage = new UserMessage("无需更新", "当前配置已经是最新状态。", "没有写入文件，也没有重启 Clash。");
            if (ApplyResult is
                {
                    FailureCode: FailureCode.PostWriteVerificationFailed,
                    RecoverySucceeded: true
                } && ApplyResult.SanitizedDetail.Contains("应用后的静态出口验证超时", StringComparison.Ordinal))
                ResultMessage = new UserMessage(
                    "配置没有完成",
                    "应用后的静态出口验证超时，原配置已经恢复。",
                    "请确认网络稳定后重试。");
            if (ApplyResult.FailureCode == FailureCode.StaticProxyAuthenticationFailed)
                await ClearCachedPasswordWithoutBlockingAsync();
            if (ApplyResult.FailureCode == FailureCode.MihomoValidationFailed &&
                ApplyResult.SanitizedDetail.StartsWith("当前使用的 Clash 节点", StringComparison.Ordinal))
            {
                ResultMessage = new UserMessage(
                    "当前线路不可用",
                    ApplyResult.SanitizedDetail,
                    "请先在 Clash Verge 中切换一个可用节点，然后返回首页重新检查电脑。");
            }
            State = ApplyResult.Success ? UiState.Succeeded : ApplyResult.FailureCode == FailureCode.RecoveryFailed ? UiState.RecoveryFailed : UiState.Failed;
            CurrentStep = 3;
            OnPropertyChanged(nameof(RecoverySummary));
        }
        finally
        {
            // 最终 Script 由 Mihomo 运行需要持有凭据；ViewModel 与 PasswordBox 不继续持有明文。
            ClearPlaintextPassword();
            IsApplying = false;
            ApplyCommand.NotifyCanExecuteChanged();
        }
    }

    private bool CanApply() => !IsApplying && State != UiState.RecoveryFailed;

    partial void OnApplyResultChanged(ApplyResult? value)
    {
        OnPropertyChanged(nameof(ResultPageTitle));
        OnPropertyChanged(nameof(CanReturnToRouting));
        OnPropertyChanged(nameof(ResultReturnText));
        OnPropertyChanged(nameof(IsSuccessResult));
        OnPropertyChanged(nameof(RecoverySummary));
        OnPropertyChanged(nameof(StaticExitSummary));
        ReturnToRoutingCommand.NotifyCanExecuteChanged();
    }

    partial void OnCurrentStepChanged(int value) => PreviousStepCommand.NotifyCanExecuteChanged();

    partial void OnStateChanged(UiState value)
    {
        OnPropertyChanged(nameof(IsEnvironmentReady));
        ApplyCommand.NotifyCanExecuteChanged();
        NextStepCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsApplyingChanged(bool value)
    {
        ApplyCommand.NotifyCanExecuteChanged();
        PreviousStepCommand.NotifyCanExecuteChanged();
    }

    partial void OnTransportModeChanged(StaticTransportMode value)
    {
        OnPropertyChanged(nameof(ConnectionModeSummary));
        OnPropertyChanged(nameof(StaticExitSummary));
    }

    partial void OnTransportPreferenceChanged(StaticTransportPreference value)
    {
        TransportMode = value switch
        {
            StaticTransportPreference.Direct => StaticTransportMode.Direct,
            StaticTransportPreference.DialerProxy => StaticTransportMode.DialerProxy,
            _ => RuntimeTransportMode(Environment?.Clash)
        };
    }

    partial void OnDialerProxyGroupChanged(string? value)
    {
        OnPropertyChanged(nameof(DialerProxyAvailable));
        OnPropertyChanged(nameof(FrontGroupSummary));
    }

    partial void OnSelectedDialerProxySelectionChanged(ProxySelection? value)
    {
        DialerProxyGroup = value?.GroupName;
        CurrentFrontNodeSummary = value is null
            ? "当前前置节点：未确定"
            : $"当前前置节点：{value.CurrentSelection}";
        FrontGroupSupportMessage = value is null
            ? "当前没有可用于链式连接的 Clash 策略组。"
            : "已识别当前前置策略组，可在直连网络不可达时使用链式模式。";
    }

    partial void OnIsTestingLatencyChanged(bool value)
    {
        TestAllNodesCommand.NotifyCanExecuteChanged();
        StopLatencyTestCommand.NotifyCanExecuteChanged();
    }

    partial void OnProxyProtocolChanged(StaticProxyProtocol value) => InvalidateStaticExitValidation();
    partial void OnProxyServerChanged(string value) => InvalidateStaticExitValidation();
    partial void OnProxyPortChanged(string value) => InvalidateStaticExitValidation();
    partial void OnProxyUsernameChanged(string value) => InvalidateStaticExitValidation();
    partial void OnProxyPasswordChanged(string value) => InvalidateStaticExitValidation();

    partial void OnActualExitIpChanged(string? value) => OnPropertyChanged(nameof(ActualExitDisplayText));

    partial void OnActualExitStateChanged(ActualExitDisplayState value) => OnPropertyChanged(nameof(ActualExitDisplayText));

    partial void OnSaveProxyTemporarilyChanged(bool value)
    {
        if (!value) _ = ClearCredentialCacheAfterOptOutAsync();
    }

    public void ClearPlaintextPassword()
    {
        _suppressProxyInputReset = true;
        try { ProxyPassword = string.Empty; }
        finally { _suppressProxyInputReset = false; }
    }

    public void CancelLatencyTests(bool detach = false)
    {
        var current = _latencyCancellation;
        if (detach && ReferenceEquals(_latencyCancellation, current))
        {
            _latencyCancellation = null;
            IsTestingLatency = false;
        }
        current?.Cancel();
    }

    private bool CanTestLatency() => !IsTestingLatency && Environment?.Clash is not null && Nodes.Count > 0;
    private bool CanStopLatency() => IsTestingLatency;
    private bool CanGoPrevious() => !IsApplying && CurrentStep > 0;

    private async Task TestCurrentNodeLatencyAsync(string pipe, string selectedNode, CancellationToken token)
    {
        var run = BeginLatencyRun(token);
        try
        {
            await _latencyTesterFactory(pipe).TestCurrentSelectedAsync(Nodes.ToArray(), selectedNode, run.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or TimeoutException or InvalidDataException)
        {
            // 当前节点测速失败只反映为本次观测结果，不降低环境支持状态。
        }
        finally { FinishLatencyRun(run); }
    }

    private async Task LoadCredentialCacheAsync()
    {
        if (!SaveProxyTemporarily) return;
        TemporaryCredentialPayload? cached;
        try { cached = await _credentialCache.LoadAsync(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        {
            cached = null;
        }
        if (cached is null) return;
        _suppressProxyInputReset = true;
        try
        {
            ProxyProtocol = cached.Protocol;
            ProxyServer = cached.Server;
            ProxyPort = cached.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
            ProxyUsername = cached.Username ?? string.Empty;
            ProxyPassword = cached.Password ?? string.Empty;
        }
        finally { _suppressProxyInputReset = false; }
        InvalidateStaticExitValidation();
        StatusText = "已从本机 DPAPI 加密缓存回填未过期的代理信息。";
    }

    private async Task<string?> SaveCredentialCacheAsync(StaticExitConfig config)
    {
        if (!SaveProxyTemporarily) return null;
        try
        {
            await _credentialCache.SaveAsync(config);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                    System.Security.Cryptography.CryptographicException or PlatformNotSupportedException or
                                    ArgumentException or InvalidOperationException or System.Security.SecurityException or
                                    System.Security.Principal.IdentityNotMappedException)
        {
            return "无法临时保存代理信息，本次仍可继续配置。";
        }
    }

    private async Task ClearCachedPasswordWithoutBlockingAsync()
    {
        try { await _credentialCache.ClearPasswordAsync(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                    System.Security.Cryptography.CryptographicException or PlatformNotSupportedException or
                                    ArgumentException or InvalidOperationException or System.Security.SecurityException or
                                    System.Security.Principal.IdentityNotMappedException)
        {
            // 缓存清理异常不能掩盖认证失败本身；UI 内存中的密码仍会立即清除。
        }
    }

    private async Task ClearCredentialCacheAfterOptOutAsync()
    {
        var deleted = await _credentialCache.ClearAsync();
        if (!deleted)
            StatusText = "已停止继续保存，但无法删除现有临时缓存；请关闭占用文件的程序后使用“清除已保存信息”重试。";
    }

    private CancellationTokenSource BeginLatencyRun(CancellationToken externalToken = default)
    {
        CancelLatencyTests(detach: true);
        var run = CancellationTokenSource.CreateLinkedTokenSource(externalToken);
        _latencyCancellation = run;
        IsTestingLatency = true;
        return run;
    }

    private void FinishLatencyRun(CancellationTokenSource run)
    {
        if (ReferenceEquals(_latencyCancellation, run))
        {
            _latencyCancellation = null;
            IsTestingLatency = false;
        }
        run.Dispose();
    }

    private void InvalidateStaticExitValidation()
    {
        if (_suppressProxyInputReset) return;
        _proxyInputRevision++;
        _staticExitReady = false;
        ActualExitIp = null;
        ActualExitState = ActualExitDisplayState.Unconfirmed;
        TransportMode = RuntimeTransportMode(Environment?.Clash);
        ApplyResult = null;
        ResultMessage = null;
        OnPropertyChanged(nameof(IsStaticExitReady));
        OnPropertyChanged(nameof(StaticExitSummary));
    }

    private StaticExitConfig CreateStaticExit(int port) => new()
    {
        Protocol = ProxyProtocol,
        Server = ProxyServer.Trim(),
        Port = port,
        Username = string.IsNullOrWhiteSpace(ProxyUsername) ? null : ProxyUsername,
        Password = string.IsNullOrEmpty(ProxyPassword) ? null : ProxyPassword
    };

    internal static StaticTransportMode RuntimeTransportMode(ClashInfo? clash)
    {
        var selected = clash?.ProxySelections.FirstOrDefault(selection =>
            selection.GroupName.Equals(RouteScriptBuilder.StaticGroupName, StringComparison.Ordinal))?.CurrentSelection;
        return selected?.Equals(RouteScriptBuilder.DialerStaticExitName, StringComparison.Ordinal) == true
            ? StaticTransportMode.DialerProxy
            : StaticTransportMode.Direct;
    }

    private static string FormatOffset(TimeSpan offset) => $"{(offset < TimeSpan.Zero ? "-" : "+")}{offset.Duration():hh\\:mm}";
}
