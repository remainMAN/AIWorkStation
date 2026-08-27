# AIWorkStationClean V1 源码审计整改报告

## 本轮修改目标

在不引入第二套部署架构、不修改用户订阅、不新增第三方依赖的前提下，修复指定的 V1 源码审计问题，并保持唯一正式流水线：

`Check → Build → Validate → Backup → Write → Reload → Verify → Recover`

本轮未读取、复制或参考 `D:\AIWorkStation`、`D:\AIWorkStationV1`。

## 基线

- Build：0 warnings，0 errors。
- Tests：43 passed，0 failed，0 skipped，43 total。
- Production Source Files：40。
- Test Source Files：6。
- Production LOC：2691。
- Test LOC：667。

## 实际修改文件

完整清单见 `CHANGED_FILES.txt`。未删除源码文件，未进行目录迁移或无关重构。

## 问题修复说明

### 双路径网络模型

最终模型：

```text
AI静态链
├─ AI静态出口-直连
└─ AI静态出口-链式
```

- Direct：本机直接连接静态代理。
- DialerProxy：通过当前活动 Clash 配置中安全确认的主策略组连接静态代理。
- 最终静态公网身份仍由后置静态代理决定。
- 直连出口不含 `dialer-proxy`；链式出口才包含该字段。
- 前置组来自 Named Pipe Controller 返回的当前真实 Selector，V1 仅接受与当前活动 Profile 同名的唯一安全主组；无法确定时只生成直连出口。
- 排除 AI WorkStation 自建组、出口和包含这些成员的策略组，防止循环。
- 直连正常时优先直连；只有网络级连接失败或超时且存在安全前置组时，才固定选择链式模式。
- 认证失败直接阻止应用，不会错误尝试链式。
- 未添加 PowerShell、Git、Python、Node、curl、dotnet 等全局进程规则。

### 配置生成与验证

- 默认只生成 `PROCESS-NAME`，移除版本相关 `PROCESS-PATH`。
- 未证明供应商 UDP 能力时不再生成 `udp: true`。
- `BaselineIssueIgnored` 不再跳过临时候选运行配置验证。
- 临时候选配置通过现有 Mihomo Named Pipe 加载，显式选择本次出口，并使用 Proxy Delay 验证真实网络路径；随后恢复原运行配置与可恢复的旧选择。
- 正式 Reload 后通过 URL 编码的 `PUT /proxies/{group}` 显式选择本次出口。
- 候选语义验证确认直连/链式字段、前置组真实存在、无循环、目标程序规则位于 MATCH 之前。

### Current Profile 与 Extension

- 仅检查当前活动 Profile 真正引用的 script、merge、rules、proxies、groups，以及全局生效的 Script/Merge。
- 未使用 Profile 的自定义脚本不再阻塞当前配置。
- 当前绑定 AI WorkStation 管理脚本时复用原 UID。
- 当前绑定标准空脚本时复用原 UID 和文件，不创建第二个 Script item。
- 当前活动 Profile 使用真实非空自定义 Extension 时继续失败关闭，不自动合并。

### Fake-IP

- 识别 `198.18.0.0/15`。
- 命中时显示为“Clash Fake-IP（非真实服务器 IP）”，不再冒充真实服务器地址。
- 未新增 DoH、DoT 或其他 DNS 系统。

### RouteVerifier

- 所有目标程序规则先由候选和正式运行配置语义验证确认存在。
- 每个程序区分 Verified、NoTrafficObserved、WrongRoute。
- 至少一个程序正确命中且其他程序无流量时成功并给出提示。
- 任一产生流量的程序走错策略时失败并进入现有 Recovery。
- 所有目标程序均无流量时失败，不宣布验证成功。

### Recovery、Reload 与事务标记

- 移除 LastWriteTime 作为 Reload 成功依据。
- Reload 成功要求 Clash/Mihomo 进程路径正确、Runtime 可读取、Named Pipe Controller 可访问。
- Recovery 先恢复并校验文件 Hash，再 Reload 并验证运行态；运行态失败时返回 RecoveryFailed。
- 损坏或半写的 transaction marker 捕获 JSON、IO 和权限异常，保留原文件并失败关闭。
- 启动时显示指定中文安全提示，不自动删除或猜测损坏 marker。
- 使用 `Local\AIWorkStation.SingleInstance` 命名互斥锁阻止双实例并发写入。

### 发布与安装包

- Release 发布目标为 `win-x64`、SelfContained、SingleFile、PublishTrimmed=false。
- 添加 `win-x64.pubxml`，包含原生库自解压设置。
- WiX `Product.wxs` 只引用 `PublishDir\AIWorkStation.exe`，不引用普通 build 输出。
- 从 `D:\AIWorkStationClean\dist\publish` 成功构建 MSI。
- 为避免修改当前系统安装状态，本轮未实际安装 MSI；安装源的 WiX 构建验证已通过。

### 测试项目与注释

- 测试项目改用标准 ProjectReference 引用正式生产 Assembly。
- 使用 InternalsVisibleTo 覆盖必要 internal 行为，没有把生产类型批量改成 public。
- 核心流程加入解释“为什么”的中文注释，没有逐行翻译或制造注释噪音。

## 新增测试

新增并通过以下行为测试：

- 直连/链式脚本字段、UDP 与 PROCESS-PATH 移除。
- 无安全前置组时直连仍可用。
- 自建组与间接成员循环防护。
- 当前运行态主策略组安全选择。
- 中文策略组 URL 编码与显式选择请求。
- BaselineIssueIgnored 仍执行临时候选验证。
- 未使用 Profile 自定义脚本不阻塞。
- 已绑定空脚本复用。
- Fake-IP 边界识别。
- RouteVerifier 部分正确流量、错误路由和零流量语义。
- 损坏 marker 失败关闭并保留。
- 文件恢复后 Reload 失败仍返回 RecoveryFailed（既有测试继续通过）。

## 最终验证结果

### Build

- Warnings：0。
- Errors：0。

### Tests

- Passed：60。
- Failed：0。
- Skipped：0。
- Total：60。

### Publish

- Result：Passed。
- Directory：`D:\AIWorkStationClean\dist\publish`。
- File count：2。
- Total size：163,605,281 bytes。
- AIWorkStation.exe：163,534,333 bytes。

### WiX

- Build：Passed。
- MSI：`D:\AIWorkStationClean\dist\AIWorkStation-1.0.0-x64.msi`。
- MSI size：53,653,504 bytes。

### UI Smoke Test

- Result：Passed。
- Executable：发布目录中的 `AIWorkStation.exe`。
- Exit code：0。
- Pages：Step 1、Step 2、Step 3、Step 4 全部成功创建与切换。
- 未执行正式 Apply，未修改真实 Clash 配置。

### Secret Scan

- Result：Passed。
- 未发现高置信度真实代理凭据、API key、token、cookie、Authorization header、private key 或 `.env`。
- 测试仅使用保留测试地址、`.example`/`.test` 域名和明确 fixture 凭据。

## 最终代码量

- Production Source Files：40。
- Test Source Files：7。
- Production LOC：2999。
- Test LOC：890。

## 仍存在但未处理的问题

None。

## 冻结状态

Build、Tests、Publish、WiX Build、UI Smoke 和 Secret Scan 均通过后，源码立即冻结。打包阶段不再修改源码。
