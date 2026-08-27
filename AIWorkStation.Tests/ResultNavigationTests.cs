using AIWorkStation.Models;
using AIWorkStation.Services;
using AIWorkStation.ViewModels;

namespace AIWorkStation.Tests;

public sealed class ResultNavigationTests
{
    [Fact]
    public async Task PreWriteFailure_CanReturnToRoutingStep()
    {
        using var temp = new TempDirectory();
        var viewModel = CreateViewModel(
            ApplyResult.Fail(FailureCode.MihomoValidationFailed, "Validate", "invalid"), temp);
        Assert.True(viewModel.ReturnToRoutingCommand.CanExecute(null));
        await viewModel.ReturnToRoutingCommand.ExecuteAsync(null);
        Assert.Equal(1, viewModel.CurrentStep);
        Assert.Equal("没有进行修改", viewModel.ResultPageTitle);
    }

    [Fact]
    public async Task RecoveredFailure_CanReturnToRoutingStep()
    {
        using var temp = new TempDirectory();
        var result = ApplyResult.Fail(FailureCode.PostWriteVerificationFailed, "Recover", "route mismatch",
            modified: true, recoveryAttempted: true, recoverySucceeded: true);
        var viewModel = CreateViewModel(result, temp);
        Assert.True(viewModel.ReturnToRoutingCommand.CanExecute(null));
        await viewModel.ReturnToRoutingCommand.ExecuteAsync(null);
        Assert.Equal(1, viewModel.CurrentStep);
        Assert.Equal("配置没有完成", viewModel.ResultPageTitle);
    }

    [Fact]
    public void RecoveryFailed_DoesNotAllowImmediateReapply()
    {
        using var temp = new TempDirectory();
        var result = ApplyResult.Fail(FailureCode.RecoveryFailed, "Recover", "critical",
            modified: true, recoveryAttempted: true, recoverySucceeded: false);
        var viewModel = CreateViewModel(result, temp);
        viewModel.State = UiState.RecoveryFailed;
        Assert.False(viewModel.ReturnToRoutingCommand.CanExecute(null));
        Assert.False(viewModel.ApplyCommand.CanExecute(null));
        Assert.Equal("当前网络配置需要检查", viewModel.ResultPageTitle);
    }

    [Fact]
    public async Task ReturnToRoutingStep_PreservesNonSecretInputs()
    {
        using var temp = new TempDirectory();
        var viewModel = CreateViewModel(
            ApplyResult.Fail(FailureCode.StaticProxyAuthenticationFailed, "Validate", "auth"), temp);
        viewModel.ProxyServer = "proxy.example";
        viewModel.ProxyPort = "1080";
        viewModel.ProxyUsername = "alice";
        viewModel.SelectedTargets.Add(new("Codex", "codex.exe", @"C:\Apps\codex.exe", true, "test"));
        await viewModel.ReturnToRoutingCommand.ExecuteAsync(null);
        Assert.Equal("proxy.example", viewModel.ProxyServer);
        Assert.Equal("1080", viewModel.ProxyPort);
        Assert.Equal("alice", viewModel.ProxyUsername);
        Assert.Single(viewModel.SelectedTargets);
    }

    private static MainViewModel CreateViewModel(ApplyResult result, TempDirectory temp)
        => new(credentialCache: new TemporaryCredentialCache(temp.File("credential-cache.bin")))
        {
            CurrentStep = 3,
            ApplyResult = result
        };
}
