using AIWorkStation.Models;

namespace AIWorkStation.Services;

public sealed class UserMessageMapper
{
    private static readonly IReadOnlyDictionary<FailureCode, UserMessage> Messages =
        new Dictionary<FailureCode, UserMessage>
        {
            [FailureCode.None] = new("配置完成", "目标软件已经通过指定静态网络分流。", "无需操作。"),
            [FailureCode.UnsupportedWindows] = new("当前系统暂不支持", "仅支持 Windows 10 / 11 x64。", "请在受支持的 Windows x64 电脑上运行。"),
            [FailureCode.ClashNotFound] = new("未检测到 Clash Verge", "请先启动 Clash Verge Rev 2.5.2。", "启动 Clash Verge 后重新检查。"),
            [FailureCode.UnsupportedClashVersion] = new("当前 Clash Verge 版本暂未支持", "当前版本仅完整支持 Clash Verge Rev 2.5.2。", "请使用 2.5.2 后重新检查。"),
            [FailureCode.MihomoNotRunning] = new("Clash 核心未运行", "没有检测到正常运行的 verge-mihomo。", "请在 Clash Verge 中确认订阅可用并重试。"),
            [FailureCode.SubscriptionNotFound] = new("当前订阅不可用", "无法从 profiles.yaml 定位并读取当前订阅。", "请在 Clash Verge 中选择一个正常订阅。"),
            [FailureCode.UnknownCustomConfiguration] = new("检测到已有自定义网络配置", "当前版本暂不支持自动配置。", "请移除未知 Script / Merge 后重新检查。"),
            [FailureCode.ApplicationNotFound] = new("没有找到目标软件", "没有检测到所选程序的可执行文件。", "请启动软件或手动搜索它的 exe。"),
            [FailureCode.StaticProxyConnectionFailed] = new("静态代理验证失败", "无法连接静态代理服务器。", "请检查服务器地址、端口和网络。"),
            [FailureCode.StaticProxyAuthenticationFailed] = new("静态代理验证失败", "用户名或密码验证失败。请检查静态代理的用户名和密码。", "更正后重新验证静态网络。"),
            [FailureCode.StaticProxyTimeout] = new("静态代理验证超时", "静态代理没有在规定时间内响应。", "请稍后重试或更换静态代理。"),
            [FailureCode.OperationCancelled] = new("操作已取消", "本次操作已由用户取消。", "确认设置后可以重新操作。"),
            [FailureCode.ExitIpLookupFailed] = new("无法确认静态出口", "代理已连接，但无法获取实际出口 IP。", "请确认代理能访问公网后重试。"),
            [FailureCode.ScriptBuildFailed] = new("无法生成分流配置", "生成 AI WorkStation 分流脚本时失败。", "请重新检查目标软件与代理信息。"),
            [FailureCode.ScriptExecutionFailed] = new("分流脚本验证失败", "生成的脚本无法正常执行。", "没有进行修改，请查看技术详情。"),
            [FailureCode.MihomoValidationFailed] = new("分流配置验证失败", "生成的分流配置无法正常加载，因此没有进行修改。", "请查看技术详情并返回修改。"),
            [FailureCode.TemporaryRuntimeLoadFailed] = new("临时验证失败", "无法安全加载候选配置或恢复原运行配置。", "没有进行修改，请先确认 Clash 运行正常。"),
            [FailureCode.ApplicationTrafficNotObserved] = new("没有检测到软件联网", "没有检测到目标软件的网络请求。", "请确认软件已经打开并执行一次联网操作。"),
            [FailureCode.ApplicationRouteMismatch] = new("软件分流验证失败", "目标软件没有命中 AI静态链。", "请关闭目标软件的已有连接后重试。"),
            [FailureCode.ExitIpMismatch] = new("静态出口验证失败", "目标连接的出口与静态代理验证结果不一致。", "请确认代理稳定后重试。"),
            [FailureCode.BackupFailed] = new("无法安全备份", "写入前备份失败，因此没有修改配置。", "请检查磁盘空间和文件权限。"),
            [FailureCode.TargetChanged] = new("当前网络配置发生变化", "检查后 Clash 配置被其他程序修改。", "请重新执行“检查电脑”。"),
            [FailureCode.WriteFailed] = new("配置写入失败", "无法安全写入 AI WorkStation 配置。", "原来的网络设置已尝试恢复。"),
            [FailureCode.ReloadFailed] = new("Clash 重新加载失败", "新配置写入后 Clash Verge 未能正常启动。", "原来的网络设置已尝试恢复。"),
            [FailureCode.PostWriteVerificationFailed] = new("配置验证未通过", "写入后的实际网络状态与预期不一致。", "原来的网络设置已尝试恢复。"),
            [FailureCode.RecoveryFailed] = new("无法确认原配置已恢复", "无法确认原来的网络配置已经完整恢复，请暂时不要继续操作。", "请查看技术详情并手动检查 Clash Verge。")
        };

    public UserMessage Map(FailureCode code) => Messages[code];
}
