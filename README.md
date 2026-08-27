# AI WorkStation

AI WorkStation 是面向 Windows 10 / 11 x64 与 Clash Verge Rev 2.5.2 的四步程序分流工具：选择程序，指定 HTTP 或 SOCKS5 静态出口，生成并验证 AI WorkStation 自有 Extension Script，安全应用并在失败时恢复。

## 用户流程

1. 检查电脑：读取 Windows、时区、Clash、Mihomo、当前订阅、节点、公网 IP、TUN、系统代理与策略组。
2. 配置分流：选择 OpenAI 预设或任意 exe，填写并验证静态代理的真实出口。
3. 确认并应用：执行唯一的 `Check → Build → Validate → Backup → Write → Reload → Verify → Recover` 流水线。
4. 查看结果：显示中文结果与可展开的脱敏技术详情。

## 安全边界

- 仅支持 Clash Verge Rev 2.5.2 的标准环境；未知 Script、Merge 或复杂覆写会被拒绝。
- 只维护包含 `AIWORKSTATION MANAGED` 标记的自有 Script。
- 密码仅保存在内存和底层必须使用的自有 Script；备份使用 Windows DPAPI CurrentUser 加密。
- 写入前验证静态出口、AIWS Script 与受管路由；以当前 `clash-verge.yaml` Effective Config 为基线生成 Runtime Candidate，再执行 `verge-mihomo -t` 和 Named Pipe 临时运行验证。
- 未被本次分流使用的 Timeout 或参数异常订阅节点只作为信息，不阻止应用，也不会被 AI WorkStation 修改、过滤或删除。
- 真实文件使用 SHA-256 复检、备份、同目录原子替换、写后验证、自动恢复和 crash marker。

## 构建与测试

```powershell
dotnet build -c Release
dotnet test -c Release
```

Release 程序位于 `AIWorkStation\bin\Release\net8.0-windows\AIWorkStation.exe`。
