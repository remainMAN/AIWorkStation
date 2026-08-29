using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AIWorkStation.Models;
using YamlDotNet.Serialization;

namespace AIWorkStation.Services;

public sealed class ApplyEngine
{
    private readonly RouteScriptBuilder _scriptBuilder;
    private readonly ScriptValidator _scriptValidator;
    private readonly MihomoValidator _mihomoValidator;
    private readonly ProfileBindingService _bindingService;
    private readonly BackupService _backupService;
    private readonly AtomicFileWriter _writer;
    private readonly ClashReloadService _reloader;
    private readonly RecoveryService _recovery;
    private readonly TransactionMarkerService _markers;
    private readonly RouteVerifier _routeVerifier;
    private readonly StaticExitTester _staticExitTester;
    private readonly MihomoLocalProxyExitTester _localExitTester;
    private readonly MihomoRuntimeConvergence _runtimeConvergence;
    private readonly Func<string, IMihomoApplyClient> _pipeFactory;
    private readonly Action<ApplyContext, ClashInfo, SubscriptionInfo> _environmentCheck;

    public ApplyEngine(
        RouteScriptBuilder? scriptBuilder = null,
        ScriptValidator? scriptValidator = null,
        MihomoValidator? mihomoValidator = null,
        ProfileBindingService? bindingService = null,
        BackupService? backupService = null,
        AtomicFileWriter? writer = null,
        ClashReloadService? reloader = null,
        RecoveryService? recovery = null,
        TransactionMarkerService? markers = null,
        RouteVerifier? routeVerifier = null,
        StaticExitTester? staticExitTester = null,
        MihomoLocalProxyExitTester? localExitTester = null,
        Func<string, IMihomoApplyClient>? pipeFactory = null,
        Action<ApplyContext, ClashInfo, SubscriptionInfo>? environmentCheck = null,
        MihomoRuntimeConvergence? runtimeConvergence = null)
    {
        _scriptBuilder = scriptBuilder ?? new RouteScriptBuilder();
        _scriptValidator = scriptValidator ?? new ScriptValidator();
        _mihomoValidator = mihomoValidator ?? new MihomoValidator();
        _bindingService = bindingService ?? new ProfileBindingService();
        _backupService = backupService ?? new BackupService();
        _writer = writer ?? new AtomicFileWriter();
        _reloader = reloader ?? new ClashReloadService();
        _markers = markers ?? new TransactionMarkerService();
        _routeVerifier = routeVerifier ?? new RouteVerifier();
        _staticExitTester = staticExitTester ?? new StaticExitTester();
        _localExitTester = localExitTester ?? new MihomoLocalProxyExitTester();
        _runtimeConvergence = runtimeConvergence ?? new MihomoRuntimeConvergence();
        _pipeFactory = pipeFactory ?? (path => new MihomoNamedPipeClient(path));
        _recovery = recovery ?? new RecoveryService(
            _writer, _reloader, _markers, path => _pipeFactory(path), _runtimeConvergence);
        _environmentCheck = environmentCheck ?? Check;
    }

    // 唯一流水线：Check → Build → Validate → Backup → Write → Reload → Verify → Recover
    public async Task<ApplyResult> ApplyAsync(
        ApplyContext context,
        IProgress<string>? progress = null,
        CancellationToken token = default)
    {
        var stage = "Check";
        var persistentWriteCount = 0;
        var persistentWriteTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var markerWritten = false;
        string? backupDirectory = null;
        TransactionMarker? marker = null;
        var effectiveRoute = context.Route;
        string? actualExitIp = string.IsNullOrWhiteSpace(context.Route.ActualExitIp)
            ? null
            : context.Route.ActualExitIp;
        byte[]? scriptBytes = null;

        try
        {
            var clash = context.Environment.Clash
                ?? throw new ApplyFailure(FailureCode.ClashNotFound, "缺少 Clash 环境快照。");
            var subscription = context.Environment.Subscription
                ?? throw new ApplyFailure(FailureCode.SubscriptionNotFound, "缺少订阅快照。");
            _environmentCheck(context, clash, subscription);

            stage = "Build";
            progress?.Report("正在生成静态分流配置…");
            var originalRuntime = await File.ReadAllTextAsync(clash.RuntimeConfigPath, token);
            var originalRuntimeSha256 = FileHash.Sha256(clash.RuntimeConfigPath);
            var originalSubscription = await File.ReadAllTextAsync(subscription.FilePath, token);

            stage = "Validate";
            progress?.Report("正在进行三次独立采样，重新确认静态代理连接、认证和实际出口…");
            var staticExit = await _staticExitTester.TestAsync(effectiveRoute.StaticExit, token);
            if (staticExit.FailureCode == FailureCode.StaticProxyAuthenticationFailed)
                throw new ApplyFailure(staticExit.FailureCode, "用户名或密码验证失败。请检查静态代理的用户名和密码。");
            var providerUnavailableWithKnownExit =
                !staticExit.Success &&
                staticExit.FailureCode == FailureCode.ExitIpLookupFailed &&
                !string.IsNullOrWhiteSpace(actualExitIp);
            if (providerUnavailableWithKnownExit)
                progress?.Report("公网出口查询服务暂时不可用；继续沿用本次应用前已确认的静态出口。");

            if (effectiveRoute.TransportMode == StaticTransportMode.Direct)
            {
                if (!staticExit.Success && !providerUnavailableWithKnownExit)
                {
                    if (IsNetworkFailure(staticExit.FailureCode) &&
                        effectiveRoute.TransportPreference == StaticTransportPreference.Auto &&
                        !string.IsNullOrWhiteSpace(effectiveRoute.DialerProxyGroup))
                    {
                        // Direct 在正式写入前发生网络故障时，只在本次操作内重建为 Dialer；一旦写入便固定模式。
                        effectiveRoute = effectiveRoute with { TransportMode = StaticTransportMode.DialerProxy };
                        actualExitIp = null;
                        progress?.Report("直连网络不稳定，正在写入前重建链式候选；真实文件仍未修改…");
                    }
                    else
                    {
                        throw new ApplyFailure(staticExit.FailureCode,
                            staticExit.SanitizedDetail ?? "静态代理直连验证失败。");
                    }
                }
                else if (staticExit.Success)
                {
                    actualExitIp = staticExit.ActualExitIp;
                    EnsureExpectedExit(effectiveRoute.ActualExitIp, actualExitIp);
                    if (staticExit.SanitizedDetail?.Contains("警告", StringComparison.Ordinal) == true)
                        progress?.Report(staticExit.SanitizedDetail);
                }
            }
            else if (!staticExit.Success && !providerUnavailableWithKnownExit &&
                     !IsNetworkFailure(staticExit.FailureCode))
            {
                throw new ApplyFailure(staticExit.FailureCode,
                    staticExit.SanitizedDetail ?? "静态代理验证失败。");
            }
            else if (!staticExit.Success)
            {
                progress?.Report("直连不可用，正在通过当前 Clash 节点验证静态出口…");
            }
            else
            {
                // 即使最终选择 Dialer，本轮直测得到的 ActualExitIp 仍是可复用的写前确认事实。
                actualExitIp = staticExit.ActualExitIp;
                EnsureExpectedExit(effectiveRoute.ActualExitIp, actualExitIp);
            }

            var script = _scriptBuilder.Build(effectiveRoute);
            scriptBytes = new UTF8Encoding(false).GetBytes(script);
            progress?.Report("正在执行脚本与配置语义验证…");
            var validationDomains = effectiveRoute.TransportMode == StaticTransportMode.DialerProxy
                ? PublicIpDetector.DefaultProviders.Select(provider => provider.Host)
                : null;
            var candidates = BuildValidationCandidates(
                _scriptValidator, _scriptBuilder, script, effectiveRoute,
                originalSubscription, originalRuntime, subscription.Name, validationDomains);
            var expectedDefinitionHashes = RecoveryService.CaptureManagedProxyDefinitionHashesFromYaml(
                candidates.RuntimeCandidate);
            _scriptValidator.ValidateSemantics(candidates.PersistenceCandidate, effectiveRoute.Targets, effectiveRoute);
            _scriptValidator.ValidateSemantics(candidates.RuntimeCandidate, effectiveRoute.Targets, effectiveRoute);

            var managedIdentifiers = effectiveRoute.Targets.Select(target => target.ExecutableName)
                .Concat([
                    RouteScriptBuilder.DirectStaticExitName,
                    RouteScriptBuilder.DialerStaticExitName,
                    RouteScriptBuilder.StaticGroupName,
                    effectiveRoute.StaticExit.Server
                ]);
            var offline = await _mihomoValidator.ValidateDeltaAsync(
                clash.MihomoProcess.ExecutablePath,
                clash.DataDirectory,
                originalRuntime,
                candidates.RuntimeCandidate,
                effectiveRoute.StaticExit,
                managedIdentifiers,
                token);
            if (!offline.Success)
                throw new ApplyFailure(FailureCode.MihomoValidationFailed, offline.SanitizedDetail);
            if (offline.BaselineIssueIgnored)
                progress?.Report("检测到订阅中存在部分异常节点，本次分流不会使用这些节点。");

            if (ShouldValidateTemporaryRuntime(offline))
            {
                var runtimeExit = await ValidateTemporaryRuntimeAsync(
                    clash, subscription, effectiveRoute,
                    originalRuntimeSha256, candidates.RuntimeCandidate, actualExitIp, progress, token);
                if (effectiveRoute.TransportMode == StaticTransportMode.DialerProxy &&
                    !string.IsNullOrWhiteSpace(runtimeExit))
                    actualExitIp = runtimeExit;
            }

            var binding = _bindingService.Prepare(clash, subscription);
            var sameScript = false;
            if (!binding.ProfilesChanged && File.Exists(binding.ScriptPath))
            {
                var currentScriptBytes = await File.ReadAllBytesAsync(binding.ScriptPath, token);
                try { sameScript = currentScriptBytes.AsSpan().SequenceEqual(scriptBytes); }
                finally { CryptographicOperations.ZeroMemory(currentScriptBytes); }
            }
            if (sameScript)
            {
                stage = "Verify";
                RecheckTargets(context, binding);
                try
                {
                    await WaitForRouteRuntimeAsync(
                        _pipeFactory(clash.ControllerPipe), effectiveRoute, token, reconcileSelection: false);
                    await WaitForPersistedRuntimeDefinitionsAsync(
                        clash.RuntimeConfigPath, expectedDefinitionHashes, token);
                }
                catch (MihomoRuntimeConvergenceException ex)
                {
                    throw new ApplyFailure(FailureCode.PostWriteVerificationFailed,
                        "磁盘 Script 已是最新版本，但当前 Mihomo Runtime 未收敛：" + ex.Message);
                }
                progress?.Report("当前配置已经是最新状态。");
                return ApplyResult.Ok(actualExitIp, effectiveRoute.TransportMode,
                    filesModified: false, noChangesRequired: true);
            }

            stage = "Backup";
            progress?.Report("正在记录原配置语义并备份即将修改的文件…");
            RecoveryBaseline recoveryBaseline;
            try
            {
                recoveryBaseline = await _runtimeConvergence.ExecuteAsync(
                    retryToken => RecoveryService.CaptureBaselineAsync(
                        clash.ProfilesPath, binding.ScriptPath, clash.RuntimeConfigPath,
                        _pipeFactory(clash.ControllerPipe), retryToken),
                    token);
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or InvalidDataException or JsonException)
            {
                throw new ApplyFailure(FailureCode.BackupFailed,
                    "无法记录恢复所需的原运行配置语义：" + ex.Message);
            }

            // profiles.yaml 可能在 Clash 正常退出时被最终保存覆盖；它必须始终进入同一备份/事务范围。
            var targets = new List<string> { binding.ScriptPath, clash.ProfilesPath };
            try
            {
                var backup = await _backupService.BackupAsync(targets, recoveryBaseline, token);
                backupDirectory = backup.Directory;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
            {
                throw new ApplyFailure(FailureCode.BackupFailed, ex.Message);
            }

            RecheckTargets(context, binding);
            marker = new TransactionMarker(
                "writing", backupDirectory, [binding.ScriptPath],
                clash.ClashProcess.ExecutablePath, clash.RuntimeConfigPath);
            await _markers.WriteAsync(marker, token);
            markerWritten = true;

            stage = "Write";
            progress?.Report("正在安全写入静态分流配置…");
            await _writer.WriteAsync(binding.ScriptPath, scriptBytes, token);
            persistentWriteCount++;
            persistentWriteTargets.Add(binding.ScriptPath);

            stage = "Reload";
            progress?.Report("正在受控重启 Clash Verge Rev…");
            try
            {
                if (!await _reloader.RestartAsync(
                        clash.ClashProcess.ExecutablePath,
                        clash.RuntimeConfigPath,
                        async shutdownToken =>
                        {
                            // Clash 已完全退出后重读最新 profiles.yaml，只补本次受管绑定并保留退出时保存的其他字段。
                            var patchedProfiles = _bindingService.PatchLatestProfiles(
                                clash.ProfilesPath, subscription.Uid, binding);
                            if (patchedProfiles is null) return;
                            // Crash recovery must cover profiles.yaml before its atomic replacement starts,
                            // while earlier failures must not roll back a file AIWS never wrote.
                            marker = marker! with { Targets = targets };
                            await _markers.WriteAsync(marker, shutdownToken);
                            await _writer.WriteAsync(clash.ProfilesPath, patchedProfiles, shutdownToken);
                            persistentWriteCount++;
                            persistentWriteTargets.Add(clash.ProfilesPath);
                        },
                        token))
                    throw new ApplyFailure(FailureCode.ReloadFailed,
                        "Clash Verge 或 verge-mihomo 未在等待时间内恢复运行。");
            }
            catch (ProfileBindingTargetChangedException ex)
            {
                throw new ApplyFailure(FailureCode.TargetChanged, ex.Message);
            }

            stage = "Verify";
            progress?.Report("正在验证写入后的有效配置与目标程序路由…");
            var persistedMismatch = GetPersistedMismatch(clash, subscription.Uid, binding, scriptBytes);
            if (persistedMismatch is not null)
                throw new ApplyFailure(FailureCode.PostWriteVerificationFailed,
                    persistedMismatch);
            var livePipePath = ClashVergeDetector.ReadRuntimeSettings(clash.RuntimeConfigPath).ControllerPipe;
            var livePipe = _pipeFactory(livePipePath);
            var selectedExit = RouteScriptBuilder.SelectedExitName(effectiveRoute);
            try
            {
                await WaitForRouteRuntimeAsync(livePipe, effectiveRoute, token);
                await WaitForPersistedRuntimeDefinitionsAsync(
                    clash.RuntimeConfigPath, expectedDefinitionHashes, token);
            }
            catch (MihomoRuntimeConvergenceException ex)
            {
                throw new ApplyFailure(FailureCode.PostWriteVerificationFailed,
                    "写入后的正式 Runtime 未在限定时间内收敛：" + ex.Message);
            }

            // 写入后重新查询实际公网出口，防止策略组名称正确但底层代理仍是旧配置。
            // 探针只在现有 Verify 阶段临时加入公网查询域名规则，随后恢复并复核正式 Runtime。
            var expectedPostWriteExit = actualExitIp ?? effectiveRoute.ActualExitIp;
            var postWriteExit = await VerifyPostWriteExitAsync(
                clash, effectiveRoute, livePipe,
                string.IsNullOrWhiteSpace(expectedPostWriteExit) ? null : expectedPostWriteExit,
                token);
            if (!postWriteExit.Success || string.IsNullOrWhiteSpace(postWriteExit.ActualExitIp) ||
                !string.IsNullOrWhiteSpace(expectedPostWriteExit) &&
                !string.Equals(expectedPostWriteExit, postWriteExit.ActualExitIp, StringComparison.OrdinalIgnoreCase))
                throw new ApplyFailure(FailureCode.PostWriteVerificationFailed,
                    postWriteExit.SanitizedDetail ?? "写入后无法确认 AI静态链的实际公网出口。");
            actualExitIp = postWriteExit.ActualExitIp;
            var liveRoute = await _routeVerifier.VerifyAsync(
                livePipe, effectiveRoute.Targets, selectedExit: selectedExit, progress: progress, token: token);
            if (!liveRoute.Success)
                throw new ApplyFailure(liveRoute.FailureCode, liveRoute.Detail);

            _markers.Delete();
            markerWritten = false;
            TryDeleteDirectory(backupDirectory);
            backupDirectory = null;
            return ApplyResult.Ok(actualExitIp, effectiveRoute.TransportMode,
                detail: CombineDetails(postWriteExit.SanitizedDetail, liveRoute.Detail),
                applicationResults: liveRoute.ApplicationResults);
        }
        catch (ApplyFailure failure)
        {
            if (persistentWriteCount == 0 || marker is null)
                return failure.Code == FailureCode.RecoveryFailed
                    ? ApplyResult.Fail(failure.Code, "Recover",
                        Sanitize(failure.Message, context.Route.StaticExit),
                        recoveryAttempted: true, recoverySucceeded: false)
                    : ApplyResult.Fail(failure.Code, stage,
                        Sanitize(failure.Message, context.Route.StaticExit));
            return await RecoverFailureAsync(
                failure.Code, stage, Sanitize(failure.Message, context.Route.StaticExit), marker,
                persistentWriteTargets);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            const string detail = "操作已取消。";
            if (persistentWriteCount == 0 || marker is null)
                return ApplyResult.Fail(FailureCode.OperationCancelled, stage, detail);
            return await RecoverFailureAsync(
                FailureCode.OperationCancelled, stage, detail, marker, persistentWriteTargets);
        }
        catch (OperationCanceledException)
        {
            var code = stage switch
            {
                "Verify" => FailureCode.PostWriteVerificationFailed,
                "Reload" => FailureCode.ReloadFailed,
                "Backup" => FailureCode.BackupFailed,
                "Write" => FailureCode.WriteFailed,
                "Build" => FailureCode.ScriptBuildFailed,
                _ => FailureCode.StaticProxyTimeout
            };
            var detail = stage == "Verify"
                ? "应用后的静态出口验证超时。"
                : "静态代理或网络操作超时。";
            if (persistentWriteCount == 0 || marker is null)
                return ApplyResult.Fail(code, stage, detail);
            return await RecoverFailureAsync(code, stage, detail, marker, persistentWriteTargets);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or
                                    InvalidOperationException or ArgumentException or JsonException or
                                    System.ComponentModel.Win32Exception or CryptographicException)
        {
            var code = stage switch
            {
                "Build" => FailureCode.ScriptBuildFailed,
                "Validate" => FailureCode.ScriptExecutionFailed,
                "Backup" => FailureCode.BackupFailed,
                "Write" => FailureCode.WriteFailed,
                "Reload" => FailureCode.ReloadFailed,
                "Verify" => FailureCode.PostWriteVerificationFailed,
                _ => FailureCode.ScriptExecutionFailed
            };
            var detail = Sanitize(ex.Message, context.Route.StaticExit);
            if (persistentWriteCount == 0 || marker is null)
                return ApplyResult.Fail(code, stage, detail);
            return await RecoverFailureAsync(code, stage, detail, marker, persistentWriteTargets);
        }
        finally
        {
            if (scriptBytes is not null) CryptographicOperations.ZeroMemory(scriptBytes);
            // Backup/Marker 已创建但尚无任何真实目标写入时，本次资源不能触发下次启动恢复。
            if (persistentWriteCount == 0)
            {
                if (markerWritten)
                {
                    try { _markers.Delete(); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                }
                if (!markerWritten || !File.Exists(_markers.MarkerPath))
                    TryDeleteDirectory(backupDirectory);
            }
        }
    }

    internal static (string PersistenceCandidate, string RuntimeCandidate) BuildValidationCandidates(
        ScriptValidator validator,
        RouteScriptBuilder builder,
        string script,
        RouteConfiguration route,
        string originalSubscription,
        string currentEffectiveRuntime,
        string profileName,
        IEnumerable<string>? validationDomains = null)
        => (validator.Execute(script, originalSubscription, profileName),
            builder.BuildRuntimeCandidate(currentEffectiveRuntime, route, validationDomains));

    internal static bool ShouldValidateTemporaryRuntime(MihomoValidationResult _) => true;

    private async Task<StaticExitTestResult> VerifyPostWriteExitAsync(
        ClashInfo clash,
        RouteConfiguration route,
        IMihomoApplyClient pipe,
        string? expectedExitIp,
        CancellationToken token)
    {
        var formalRuntime = await File.ReadAllTextAsync(clash.RuntimeConfigPath, token);
        var probeRuntime = _scriptBuilder.BuildRuntimeCandidate(
            formalRuntime,
            route,
            PublicIpDetector.DefaultProviders.Select(provider => provider.Host));
        Exception? probeFailure = null;
        Exception? restoreFailure = null;
        StaticExitTestResult? result = null;
        try
        {
            await _runtimeConvergence.ExecuteAsync(
                retryToken => pipe.PutInlineConfigAsync(probeRuntime, retryToken), token);
            await WaitForRouteRuntimeAsync(pipe, route, token);
            result = await _localExitTester.TestAsync(
                clash.MixedPort, clash.HttpPort, clash.SocksPort,
                expectedExitIp,
                token);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException or
                                   JsonException or InvalidDataException or InvalidOperationException)
        {
            probeFailure = ex;
        }
        finally
        {
            try
            {
                await _runtimeConvergence.ExecuteAsync(
                    retryToken => pipe.PutInlineConfigAsync(formalRuntime, retryToken),
                    CancellationToken.None);
                await WaitForRouteRuntimeAsync(pipe, route, CancellationToken.None);
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException or
                                       JsonException or InvalidDataException or InvalidOperationException)
            {
                restoreFailure = ex;
            }
        }

        if (restoreFailure is not null)
            throw new ApplyFailure(FailureCode.PostWriteVerificationFailed,
                "写入后出口探测结束，但正式 Runtime 未能收敛恢复：" +
                Sanitize(restoreFailure.Message, route.StaticExit));
        if (probeFailure is OperationCanceledException cancellation) throw cancellation;
        if (probeFailure is not null)
            throw new ApplyFailure(FailureCode.PostWriteVerificationFailed,
                Sanitize(probeFailure.Message, route.StaticExit));
        var verified = result ?? new StaticExitTestResult(
            false, null, FailureCode.PostWriteVerificationFailed,
            "写入后无法确认 AI静态链的实际公网出口。");
        if (!verified.Success && verified.FailureCode == FailureCode.ExitIpLookupFailed &&
            !string.IsNullOrWhiteSpace(expectedExitIp))
            return new StaticExitTestResult(
                true,
                expectedExitIp,
                FailureCode.None,
                "配置已生效；公网出口查询服务暂时不可用，沿用本次应用前已确认的静态出口。");
        return verified;
    }

    private async Task<string?> ValidateTemporaryRuntimeAsync(
        ClashInfo clash,
        SubscriptionInfo subscription,
        RouteConfiguration route,
        string originalRuntimeSha256,
        string runtimeCandidate,
        string? knownExitIp,
        IProgress<string>? progress,
        CancellationToken token)
    {
        progress?.Report("正在临时验证候选配置；真实文件尚未修改…");
        var restoreRuntime = await File.ReadAllTextAsync(clash.RuntimeConfigPath, token);
        if (!string.Equals(FileHash.Sha256(clash.RuntimeConfigPath), originalRuntimeSha256, StringComparison.Ordinal))
            throw new ApplyFailure(FailureCode.TemporaryRuntimeLoadFailed,
                "临时验证前 Runtime YAML 已变化，请重新检查环境。");
        var pipe = _pipeFactory(clash.ControllerPipe);
        var baseline = await _runtimeConvergence.ExecuteAsync(
            retryToken => CaptureTemporaryBaselineAsync(clash, subscription, pipe, retryToken),
            token);
        if (!string.Equals(FileHash.Sha256(clash.RuntimeConfigPath), originalRuntimeSha256, StringComparison.Ordinal))
            throw new ApplyFailure(FailureCode.TemporaryRuntimeLoadFailed,
                "记录 Runtime 基线期间 YAML 已变化，请重新检查环境。");
        Exception? candidateFailure = null;
        string? actualExitIp = null;
        try
        {
            await _runtimeConvergence.ExecuteAsync(
                retryToken => pipe.PutInlineConfigAsync(runtimeCandidate, retryToken), token);
            await WaitForRouteRuntimeAsync(pipe, route, token);
            if (route.TransportMode == StaticTransportMode.DialerProxy)
            {
                var actual = await _localExitTester.TestAsync(
                    clash.MixedPort, clash.HttpPort, clash.SocksPort,
                    string.IsNullOrWhiteSpace(knownExitIp) ? null : knownExitIp,
                    token);
                if (!actual.Success && actual.FailureCode == FailureCode.ExitIpLookupFailed &&
                    !string.IsNullOrWhiteSpace(knownExitIp))
                {
                    actualExitIp = knownExitIp;
                    progress?.Report("候选 Runtime 已加载；公网出口查询服务暂时不可用，沿用先前已确认的出口。");
                }
                else if (!actual.Success)
                    throw new ApplyFailure(actual.FailureCode,
                        actual.SanitizedDetail ?? "链式连接可达，但无法确认实际公网出口。");
                else actualExitIp = actual.ActualExitIp;
            }
        }
        catch (Exception ex) when (ex is ApplyFailure or IOException or TimeoutException or
                                    OperationCanceledException or JsonException or InvalidDataException)
        {
            candidateFailure = ex;
        }

        var restored = await RestoreTemporaryRuntimeAsync(
            clash, subscription, pipe, baseline, restoreRuntime);
        if (!restored)
            throw new ApplyFailure(FailureCode.RecoveryFailed,
                "临时验证后无法确认原运行配置已等价恢复；当前网络状态无法确认，请暂时不要继续配置。");

        if (candidateFailure is ApplyFailure applyFailure) throw applyFailure;
        if (candidateFailure is OperationCanceledException cancellation) throw cancellation;
        if (candidateFailure is not null)
            throw new ApplyFailure(FailureCode.TemporaryRuntimeLoadFailed,
                Sanitize(candidateFailure.Message, route.StaticExit));
        return actualExitIp;
    }

    private static async Task<TemporaryRuntimeBaseline> CaptureTemporaryBaselineAsync(
        ClashInfo clash,
        SubscriptionInfo subscription,
        IMihomoRuntimeClient pipe,
        CancellationToken token)
    {
        var runtime = await RecoveryService.CaptureRuntimeSemanticBaselineAsync(pipe, token);
        var extensionExists = subscription.ScriptPath is not null && File.Exists(subscription.ScriptPath);
        var extensionHash = extensionExists ? FileHash.Sha256(subscription.ScriptPath!) : null;
        return new TemporaryRuntimeBaseline(
            ReadCurrentProfileUid(clash.ProfilesPath),
            subscription.ScriptPath,
            extensionExists,
            extensionHash,
            runtime);
    }

    private async Task<bool> RestoreTemporaryRuntimeAsync(
        ClashInfo clash,
        SubscriptionInfo subscription,
        IMihomoApplyClient pipe,
        TemporaryRuntimeBaseline baseline,
        string originalRuntime)
    {
        try
        {
            await _runtimeConvergence.ExecuteAsync(
                retryToken => pipe.PutInlineConfigAsync(originalRuntime, retryToken),
                CancellationToken.None);
            await _runtimeConvergence.WaitAsync(
                pipe,
                runtime =>
                {
                    var mismatch = GetRuntimeSemanticMismatch(baseline.Runtime, runtime);
                    return (mismatch is null, mismatch);
                },
                async (runtime, retryToken) =>
                {
                    if (!baseline.Runtime.ManagedGroupExists || !runtime.ManagedGroupExists ||
                        string.IsNullOrWhiteSpace(baseline.Runtime.ManagedGroupSelection) ||
                        string.Equals(runtime.ManagedGroupSelection, baseline.Runtime.ManagedGroupSelection,
                            StringComparison.Ordinal)) return;
                    try
                    {
                        await pipe.SelectProxyAsync(
                            RouteScriptBuilder.StaticGroupName,
                            baseline.Runtime.ManagedGroupSelection,
                            retryToken);
                    }
                    catch (MihomoControllerException ex) when (ex.StatusCode == 404)
                    {
                        // 采样后 Runtime 又切换了一代；下一次采样继续判断。
                    }
                },
                CancellationToken.None);
            if (!string.Equals(ReadCurrentProfileUid(clash.ProfilesPath), baseline.CurrentProfileUid,
                    StringComparison.Ordinal)) return false;
            if (!string.Equals(subscription.Uid, baseline.CurrentProfileUid, StringComparison.Ordinal)) return false;

            var exists = baseline.ExtensionPath is not null && File.Exists(baseline.ExtensionPath);
            if (exists != baseline.ExtensionExisted) return false;
            if (baseline.ExtensionExisted &&
                !string.Equals(FileHash.Sha256(baseline.ExtensionPath!), baseline.ExtensionSha256,
                    StringComparison.Ordinal)) return false;
            return true;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException or
                                    JsonException or InvalidDataException or UnauthorizedAccessException or
                                    InvalidOperationException or ArgumentException or YamlDotNet.Core.YamlException)
        {
            return false;
        }
    }

    private static void Check(ApplyContext context, ClashInfo clash, SubscriptionInfo subscription)
    {
        var liveClash = ClashVergeDetector.FindSingleProcess("clash-verge");
        var liveMihomo = ClashVergeDetector.FindSingleProcess("verge-mihomo");
        if (liveClash is null ||
            !string.Equals(liveClash.ExecutablePath, clash.ClashProcess.ExecutablePath, StringComparison.OrdinalIgnoreCase))
            throw new ApplyFailure(FailureCode.ClashNotFound, "Clash Verge 已停止或 executable 发生变化。");
        if (liveMihomo is null ||
            !string.Equals(liveMihomo.ExecutablePath, clash.MihomoProcess.ExecutablePath, StringComparison.OrdinalIgnoreCase))
            throw new ApplyFailure(FailureCode.MihomoNotRunning, "verge-mihomo 已停止或 executable 发生变化。");
        if (!File.Exists(context.ProfilesFingerprint.Path) ||
            FileHash.Sha256(context.ProfilesFingerprint.Path) != context.ProfilesFingerprint.Sha256)
            throw new ApplyFailure(FailureCode.TargetChanged, "profiles.yaml SHA-256 已变化。");
        if (!string.Equals(subscription.Uid, ReadCurrentProfileUid(clash.ProfilesPath), StringComparison.Ordinal))
            throw new ApplyFailure(FailureCode.TargetChanged, "Current Profile UID 已变化。");
        if (context.ScriptFingerprint is not null &&
            (!File.Exists(context.ScriptFingerprint.Path) ||
             FileHash.Sha256(context.ScriptFingerprint.Path) != context.ScriptFingerprint.Sha256))
            throw new ApplyFailure(FailureCode.TargetChanged, "AIWS Extension SHA-256 已变化。");
    }

    private static void RecheckTargets(ApplyContext context, ProfileBindingPlan binding)
    {
        if (FileHash.Sha256(context.ProfilesFingerprint.Path) != context.ProfilesFingerprint.Sha256)
            throw new ApplyFailure(FailureCode.TargetChanged, "Backup 后 profiles.yaml SHA-256 已变化。");
        if (context.ScriptFingerprint is not null &&
            FileHash.Sha256(context.ScriptFingerprint.Path) != context.ScriptFingerprint.Sha256)
            throw new ApplyFailure(FailureCode.TargetChanged, "Backup 后 AIWS Extension SHA-256 已变化。");
        if (context.ScriptFingerprint is null && File.Exists(binding.ScriptPath))
            throw new ApplyFailure(FailureCode.TargetChanged, "新 Script 目标被外部创建。");
    }

    private static string? GetPersistedMismatch(
        ClashInfo clash,
        string currentUid,
        ProfileBindingPlan binding,
        ReadOnlySpan<byte> expectedScriptBytes)
    {
        if (!File.Exists(binding.ScriptPath)) return "AI WorkStation Script 文件不存在。";
        var persistedScriptBytes = File.ReadAllBytes(binding.ScriptPath);
        try
        {
            if (!persistedScriptBytes.AsSpan().SequenceEqual(expectedScriptBytes))
                return "持久化 Script 内容与本次已验证候选不一致。";
        }
        finally
        {
            CryptographicOperations.ZeroMemory(persistedScriptBytes);
        }
        var yaml = new DeserializerBuilder().IgnoreUnmatchedProperties().Build();
        var profiles = yaml.Deserialize<ProfilesDocument>(File.ReadAllText(clash.ProfilesPath));
        if (profiles?.Current != currentUid) return "持久化 Current Profile UID 与本次目标不一致。";
        var current = profiles.Items.SingleOrDefault(item => item.Uid == currentUid);
        if (current?.Option?.Script != binding.ScriptUid) return "当前 Profile 未绑定本次 AI WorkStation Script。";
        var scriptItems = profiles.Items
            .Where(item => string.Equals(item.Uid, binding.ScriptUid, StringComparison.Ordinal))
            .ToArray();
        if (scriptItems.Length != 1) return "profiles.yaml 中的 AI WorkStation Script 项缺失或重复。";
        if (!string.Equals(scriptItems[0].Type, "script", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(scriptItems[0].File, Path.GetFileName(binding.ScriptPath), StringComparison.OrdinalIgnoreCase))
            return "profiles.yaml 中的 AI WorkStation Script 项类型或文件名不一致。";
        return null;
    }

    private async Task<ApplyResult> RecoverFailureAsync(
        FailureCode originalCode,
        string stage,
        string detail,
        TransactionMarker marker,
        IReadOnlyCollection<string> persistentWriteTargets)
    {
        var recovered = await _recovery.RecoverAsync(
            marker,
            persistentWriteTargets,
            verifyOriginalBaseline: originalCode != FailureCode.TargetChanged,
            CancellationToken.None);
        return recovered
            ? ApplyResult.Fail(originalCode, "Recover", detail,
                modified: true, recoveryAttempted: true, recoverySucceeded: true)
            : ApplyResult.Fail(FailureCode.RecoveryFailed, "Recover", detail,
                modified: true, recoveryAttempted: true, recoverySucceeded: false);
    }

    private static void EnsureExpectedExit(string expected, string? actual)
    {
        if (!string.IsNullOrWhiteSpace(expected) &&
            !string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
            throw new ApplyFailure(FailureCode.ExitIpMismatch,
                "静态代理实际出口 IP 已发生变化，请重新验证。");
    }

    private static bool IsNetworkFailure(FailureCode code)
        => code is FailureCode.StaticProxyConnectionFailed or FailureCode.StaticProxyTimeout;

    private async Task WaitForPersistedRuntimeDefinitionsAsync(
        string runtimeConfigPath,
        IReadOnlyDictionary<string, string> expectedDefinitionHashes,
        CancellationToken token)
    {
        await _runtimeConvergence.ExecuteAsync(
            retryToken =>
            {
                retryToken.ThrowIfCancellationRequested();
                try
                {
                    var actual = RecoveryService.CaptureManagedProxyDefinitionHashes(runtimeConfigPath);
                    if (expectedDefinitionHashes.Count == actual.Count &&
                        expectedDefinitionHashes.All(item => actual.TryGetValue(item.Key, out var hash) &&
                                                             string.Equals(item.Value, hash, StringComparison.Ordinal)))
                        return Task.CompletedTask;
                    throw new IOException(
                        $"持久化 Runtime 受管代理定义尚未收敛；期望 {expectedDefinitionHashes.Count} 项，实际 {actual.Count} 项。");
                }
                catch (Exception ex) when (ex is InvalidDataException or YamlDotNet.Core.YamlException)
                {
                    throw new IOException("持久化 Runtime YAML 尚未完整生成。", ex);
                }
            },
            token);
    }

    private async Task<RuntimeSemanticBaseline> WaitForRouteRuntimeAsync(
        IMihomoApplyClient pipe,
        RouteConfiguration route,
        CancellationToken token,
        bool reconcileSelection = true)
    {
        var selectedExit = RouteScriptBuilder.SelectedExitName(route);
        return await _runtimeConvergence.WaitAsync(
            pipe,
            runtime =>
            {
                var mismatch = GetRuntimeRouteMismatch(runtime, route);
                return (mismatch is null, mismatch);
            },
            async (runtime, retryToken) =>
            {
                if (!reconcileSelection || !runtime.ManagedGroupExists ||
                    string.Equals(runtime.ManagedGroupSelection, selectedExit, StringComparison.Ordinal)) return;
                try
                {
                    await pipe.SelectProxyAsync(RouteScriptBuilder.StaticGroupName, selectedExit, retryToken);
                }
                catch (MihomoControllerException ex) when (ex.StatusCode == 404)
                {
                    // Runtime 正在换代时 Selector 可能在相邻采样间短暂消失；继续读取 live runtime。
                }
            },
            token);
    }

    private static string? GetRuntimeRouteMismatch(
        RuntimeSemanticBaseline runtime,
        RouteConfiguration route)
    {
        var expectedProxyNames = string.IsNullOrWhiteSpace(route.DialerProxyGroup)
            ? new[] { RouteScriptBuilder.DirectStaticExitName }
            : new[] { RouteScriptBuilder.DirectStaticExitName, RouteScriptBuilder.DialerStaticExitName };
        if (!runtime.ManagedProxyNames.SequenceEqual(expectedProxyNames, StringComparer.Ordinal))
            return $"受管代理尚未收敛；期望 [{string.Join(", ", expectedProxyNames)}]，实际 [{string.Join(", ", runtime.ManagedProxyNames)}]。";
        if (!runtime.ManagedGroupExists) return $"受管策略组 {RouteScriptBuilder.StaticGroupName} 尚未出现。";

        var expectedMembers = string.IsNullOrWhiteSpace(route.DialerProxyGroup)
            ? new[] { RouteScriptBuilder.DirectStaticExitName }
            : route.TransportMode == StaticTransportMode.DialerProxy
                ? new[] { RouteScriptBuilder.DialerStaticExitName, RouteScriptBuilder.DirectStaticExitName }
                : new[] { RouteScriptBuilder.DirectStaticExitName, RouteScriptBuilder.DialerStaticExitName };
        if (!runtime.ManagedGroupMembers.SequenceEqual(expectedMembers, StringComparer.Ordinal))
            return $"受管策略组成员尚未收敛；期望 [{string.Join(", ", expectedMembers)}]，实际 [{string.Join(", ", runtime.ManagedGroupMembers)}]。";
        var selectedExit = RouteScriptBuilder.SelectedExitName(route);
        if (!string.Equals(runtime.ManagedGroupSelection, selectedExit, StringComparison.Ordinal))
            return $"受管策略组选择尚未收敛；期望 {selectedExit}，实际 {runtime.ManagedGroupSelection ?? "None"}。";

        var expectedExecutables = route.Targets.Select(target => target.ExecutableName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (runtime.ManagedRules.Count != expectedExecutables.Length ||
            !expectedExecutables.All(executable => runtime.ManagedRules.Any(rule => IsManagedProcessRule(rule, executable))))
        {
            var missing = expectedExecutables
                .Where(executable => !runtime.ManagedRules.Any(rule => IsManagedProcessRule(rule, executable)))
                .ToArray();
            return missing.Length > 0
                ? "目标程序规则尚未收敛；缺少 " + string.Join(", ", missing) + "。"
                : $"目标程序规则数量尚未收敛；期望 {expectedExecutables.Length}，实际 {runtime.ManagedRules.Count}。";
        }
        return null;
    }

    internal static string? GetRuntimeSemanticMismatch(
        RuntimeSemanticBaseline expected,
        RuntimeSemanticBaseline actual)
    {
        if (expected.ManagedGroupExists != actual.ManagedGroupExists)
            return "受管策略组存在状态与基线不一致。";
        if (!string.Equals(expected.ManagedGroupSelection, actual.ManagedGroupSelection, StringComparison.Ordinal))
            return $"受管策略组选择与基线不一致；期望 {expected.ManagedGroupSelection ?? "None"}，实际 {actual.ManagedGroupSelection ?? "None"}。";
        if (!expected.ManagedProxyNames.SequenceEqual(actual.ManagedProxyNames, StringComparer.Ordinal))
            return "受管代理名称与基线不一致。";
        if (!expected.ManagedGroupMembers.SequenceEqual(actual.ManagedGroupMembers, StringComparer.Ordinal))
            return "受管策略组成员与基线不一致。";
        if (!expected.ManagedRules.SequenceEqual(actual.ManagedRules, StringComparer.Ordinal))
            return "受管程序规则与基线不一致。";
        if (expected.ManagedProxyDefinitionHashes.Count != actual.ManagedProxyDefinitionHashes.Count ||
            expected.ManagedProxyDefinitionHashes.Any(item =>
                !actual.ManagedProxyDefinitionHashes.TryGetValue(item.Key, out var hash) ||
                !string.Equals(item.Value, hash, StringComparison.Ordinal)))
            return "受管代理定义与持久化基线不一致。";
        return null;
    }

    private static bool IsManagedProcessRule(string semantic, string executable)
    {
        if (string.Equals(semantic,
                $"PROCESS-NAME,{executable},{RouteScriptBuilder.StaticGroupName}",
                StringComparison.OrdinalIgnoreCase)) return true;
        try
        {
            var values = JsonSerializer.Deserialize<string[]>(semantic);
            return values is { Length: 3 } &&
                   values[0].Contains("Process", StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(values[1], executable, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(values[2], RouteScriptBuilder.StaticGroupName, StringComparison.Ordinal);
        }
        catch (JsonException) { return false; }
    }

    private static string? ReadCurrentProfileUid(string profilesPath)
        => new DeserializerBuilder().IgnoreUnmatchedProperties().Build()
            .Deserialize<ProfilesDocument>(File.ReadAllText(profilesPath))?.Current;

    private static string Sanitize(string value, StaticExitConfig config)
    {
        if (!string.IsNullOrEmpty(config.Password))
            value = value.Replace(config.Password, "***", StringComparison.Ordinal);
        if (!string.IsNullOrEmpty(config.Username))
            value = value.Replace(config.Username, "***", StringComparison.Ordinal);
        if (!string.IsNullOrEmpty(config.Server))
            value = value.Replace(config.Server, "***", StringComparison.OrdinalIgnoreCase);
        return value.Length <= 2000 ? value : value[..2000];
    }

    private static string CombineDetails(params string?[] details)
        => string.Join(Environment.NewLine, details
            .Where(detail => !string.IsNullOrWhiteSpace(detail))
            .Distinct(StringComparer.Ordinal));

    private static void TryDeleteDirectory(string? path)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private sealed record TemporaryRuntimeBaseline(
        string? CurrentProfileUid,
        string? ExtensionPath,
        bool ExtensionExisted,
        string? ExtensionSha256,
        RuntimeSemanticBaseline Runtime);

    private sealed class ApplyFailure(FailureCode code, string message) : Exception(message)
    {
        public FailureCode Code { get; } = code;
    }
}
