# AIWorkStation V1 UI Implementation Acceptance Report

审查基线：`UI_ACCEPTANCE.md`。状态为 Pass / Fail / N/A；每个清单项按原文件行号单独记录。

## 1. Scope protection

| Line | Status | Result |
|---:|:---:|---|
| 7 | Pass | 保持检查电脑、配置分流、确认并应用、结果四步。 |
| 8 | Pass | 未增加第五页、设置中心、账户流或模态流程。 |
| 9 | Pass | 未增加必填项、确认、Gate、Readiness、Eligibility、Unsupported 或流水线。 |
| 10 | Pass | Success、NotObserved、WrongRoute、失败及恢复语义未改。 |
| 11 | Pass | Direct、DialerProxy、应用发现、凭据缓存、延迟行为未改。 |
| 12 | Pass | 普通界面不显示所列内部术语。 |
| 13 | Pass | 技术详情默认折叠。 |
| 14 | Pass | 密码使用 PasswordBox，生产结果页不回显凭据。 |

## 2. Global shell

| Line | Status | Result |
|---:|:---:|---|
| 18 | Pass | 标题和一句用途说明清晰可见。 |
| 19 | Pass | 当前、完成、后续步骤使用边框、底色与文字区分。 |
| 20 | Pass | 状态同时使用文字、形状和颜色。 |
| 21 | Pass | 每页保留一个主视觉动作。 |
| 22 | Pass | 主次动作位于右下动作区。 |
| 23 | Pass | 单一工作区表面，卡片层级受控。 |
| 24 | Pass | 1 DIP 边框、克制圆角，无阴影、模糊、Mica、Acrylic 或 GPU 特效。 |

## 3. Step 1

| Line | Status | Result |
|---:|:---:|---|
| 28 | Pass | 标题为“检查电脑”。 |
| 29 | Pass | 副标题包含指定句。 |
| 30 | Pass | Ready 夹具显示“检查完成，可以继续配置。”。 |
| 31 | Pass | 三个摘要区域覆盖要求的系统、Clash 与网络事实。 |
| 32 | Pass | 前置策略组与当前节点分别显示。 |
| 33 | Pass | 节点表包含七个要求列。 |
| 34 | Pass | Fake-IP 在服务器 IP 列以明确文字区分且不作为错误。 |
| 35 | Fail | 节点状态目前为文字，尚未在每一行增加状态图标。 |
| 36 | Pass | 无“合格/不合格”标签。 |
| 37 | Pass | 测试全部节点与停止测试仍绑定原命令。 |
| 38 | Pass | 明确说明延迟不影响应用条件。 |
| 39 | Pass | 节点表 MinHeight 为 240 DIP。 |
| 40 | Pass | 高度通过页面滚动与 240 DIP 表格占用可用空间。 |
| 41 | Pass | 重新检查为次按钮，下一步为主按钮。 |

## 4. Step 2

| Line | Status | Result |
|---:|:---:|---|
| 45 | Pass | 标题为“配置分流”。 |
| 46 | Pass | 使用指定副标题。 |
| 47 | Pass | 常规宽度明确分隔目标软件与静态出口。 |
| 48 | Pass | OpenAI 预设使用推荐卡片和主选择动作。 |
| 49 | Pass | 描述为“ChatGPT 和 Codex 共用一个静态出口”。 |
| 50 | Pass | 测试夹具及现有预设均显示 ChatGPT 与 Codex。 |
| 51 | Pass | 搜索、浏览 EXE、结果、已选和移除均保留。 |
| 52 | Pass | 已选项显示展示名与 exe 名。 |
| 53 | Pass | 协议、服务器、端口、可选用户名和密码完整。 |
| 54 | Fail | 当前 ViewModel 没有字段级错误集合，界面仅保留区域级验证反馈。 |
| 55 | Pass | 缓存复选框使用指定文案。 |
| 56 | Pass | 普通隐私文案不含 DPAPI、Script、Candidate 或 Mihomo。 |
| 57 | Pass | 清除信息为三级文字动作。 |
| 58 | Pass | 验证按钮为正常 40 DIP 控件。 |
| 59 | Pass | 未验证状态使用蓝灰中性信息面板。 |
| 60 | Pass | 成功状态显示真实 ActualExitIp。 |
| 61 | Pass | 自动（推荐）、直连、经当前 Clash 节点连接始终可见。 |
| 62 | Pass | Auto 解析结果通过 ConnectionModeSummary 显示。 |
| 63 | Pass | 选择绑定 TransportPreference，不由视觉层覆盖。 |
| 64 | Pass | 链式显示前置组与当前前置节点。 |
| 65 | Pass | 链式显示中文可读路径。 |
| 66 | Pass | 普通界面不显示内部枚举或 `dialer-proxy`。 |
| 67 | Pass | 链式信息只读展示；未新增阻塞状态。 |
| 68 | Pass | 上一步为次按钮，验证成功时确认配置为主按钮。 |

## 5. Step 3

| Line | Status | Result |
|---:|:---:|---|
| 72 | Pass | 标题为“确认并应用”。 |
| 73 | Pass | 汇总目标软件、实际出口、连接方式及其他程序。 |
| 74 | Pass | 链式附加前置组、当前节点和路径。 |
| 75 | Pass | 直连时链式区块折叠。 |
| 76 | Pass | 文案为“保持当前网络，不受影响”。 |
| 77 | Pass | 恢复说明紧邻摘要。 |
| 78 | Pass | 说明先备份并在写入后失败时进入现有恢复流程。 |
| 79 | Pass | 不显示开发者门禁术语。 |
| 80 | Pass | 返回修改为次按钮，正式应用为唯一主动作。 |
| 81 | Pass | 摘要最大宽度 780，内容紧凑。 |

## 6. Step 4

| Line | Status | Result |
|---:|:---:|---|
| 87 | Pass | Applying 标题为“正在应用配置”。 |
| 88 | Pass | 绑定现有 StatusText 显示实时阶段。 |
| 89 | Pass | 说明覆盖检查、生成、验证、保存、重载及路由确认。 |
| 90 | Pass | 仅使用标准低动态 ProgressBar。 |
| 91 | Pass | Apply 防重复仍由原行为控制。 |
| 95 | Pass | Success 标题为“配置完成”。 |
| 96 | Pass | 显示目标、方式、实际出口，链式时显示路径。 |
| 97 | Pass | 提供明确“完成”动作。 |
| 101 | Pass | NotObserved 标题覆盖为“配置已应用”。 |
| 102 | Pass | 显示未观察到新目标流量。 |
| 103 | Pass | 明确写明这不表示配置失败。 |
| 104 | Pass | 提示打开并正常使用目标软件后再检查。 |
| 105 | Pass | 采用信息/成功语义，不使用错误或警告语义。 |
| 109 | Pass | 写前失败标题为“没有进行修改”。 |
| 110 | Pass | 显示具体中文原因。 |
| 111 | Pass | FilesModified=false 时显示当前网络配置未修改。 |
| 112 | Pass | 返回修改可用。 |
| 116 | Pass | 恢复成功失败标题为“配置没有完成”。 |
| 117 | Pass | 恢复行显示“原来的网络配置已经恢复。”。 |
| 118 | Pass | 显示具体失败原因。 |
| 119 | Pass | 超时文案使用指定准确含义。 |
| 120 | Pass | 未显示“被其他程序修改”。 |
| 121 | Pass | 返回配置可用。 |
| 125 | Pass | RecoveryFailed 标题为“当前网络状态需要检查”。 |
| 126 | Pass | 明确提示不要继续应用配置。 |
| 127 | Pass | 返回首页可用。 |
| 128 | Pass | 技术详情可展开。 |
| 129 | Pass | 使用错误图标、标题、正文和边框，不依赖红色。 |

## 7. Design tokens and visual quality

| Line | Status | Result |
|---:|:---:|---|
| 133 | Pass | Segoe UI，中文由系统兼容字体回退。 |
| 134 | Pass | 结果标题 28 DIP SemiBold。 |
| 135 | Pass | 页面标题 26 DIP SemiBold，处于批准范围。 |
| 136 | Pass | 正文 14 DIP，辅助文字不低于 12 DIP。 |
| 137 | Pass | 主色 `#2563EB` 用于当前步骤和主动作。 |
| 138 | Pass | Canvas、Surface、Border、Ink、Muted 角色集中定义。 |
| 139 | Pass | 中性、成功、警告、错误使用不同图标/文字/边框。 |
| 140 | Pass | 普通控件最小高度 40 DIP。 |
| 141 | Pass | 紧凑文字动作最小 36 × 36 DIP。 |
| 142 | Pass | 间距使用 4 DIP 基准的倍数。 |
| 143 | Pass | 卡片圆角 8，工作区圆角受控。 |
| 144 | Pass | 无渐变、饱和大面板、游戏化或 Web 仪表盘样式。 |

## 8. Keyboard and accessibility

| Line | Status | Result |
|---:|:---:|---|
| 148 | Pass | 使用标准可聚焦 WPF 控件。 |
| 149 | Pass | XAML 顺序与视觉阅读顺序一致。 |
| 150 | Pass | 集中定义 2 DIP 键盘焦点轮廓。 |
| 151 | Pass | 代理字段有可见标签和 AutomationProperties.Name。 |
| 152 | Pass | 图标仅作状态辅助；动作均有文字名称。 |
| 153 | Pass | 移除动作自动化名称包含应用名。 |
| 154 | Pass | 验证与结果区域使用 Polite LiveSetting。 |
| 155 | Pass | 区域级错误与对应代理/结果区域相邻。 |
| 156 | Pass | 状态包含文字/图标，不只依赖颜色。 |
| 157 | Pass | 设计令牌采用高对比深色正文和明确边界。 |
| 158 | Pass | 标准 ListView 支持键盘行导航。 |
| 159 | Pass | 标准 Expander 可由键盘展开读取。 |
| 160 | N/A | 本轮未执行 Windows High Contrast 人工走查。 |
| 161 | Pass | 必要信息均在界面正文，Tooltip 仅作补充。 |

## 9. Window, DPI, and small-screen

| Line | Status | Result |
|---:|:---:|---|
| 165 | Fail | 初始尺寸为 1180 × 760；尚未增加显式 WorkArea clamp 代码。 |
| 166 | Pass | 最小尺寸为 960 × 640。 |
| 167 | Pass | 1366 × 768 截图可用，动作通过页面滚动可达。 |
| 168 | N/A | 未单独生成 1440 × 900 截图。 |
| 169 | N/A | 未单独生成 1920 × 1080 截图；MaxWidth=1240 已实现。 |
| 170 | Pass | 125% 截图及滚动可达性通过。 |
| 171 | Pass | Step 2 在有效宽度不足 1080 DIP 时重排为单列。 |
| 172 | Pass | 单列重排不增加页面或业务流。 |
| 173 | Pass | 四步条始终保留文字并均分显示。 |
| 174 | Pass | 节点表 MinHeight=240。 |
| 175 | Pass | 状态、帮助、结果和路径文字启用换行。 |
| 176 | Pass | 主按钮始终保留文字。 |
| 177 | Pass | 横向滚动限制在列表/节点表。 |
| 178 | Pass | 页面采用一个主纵向滚动区，内部列表保留标准导航。 |

## 10. WPF and SoftwareOnly feasibility

| Line | Status | Result |
|---:|:---:|---|
| 182 | Pass | 仅使用稳定标准 WPF 控件。 |
| 183 | Pass | 无 Mica、Acrylic、模糊、透明窗、Shader 或高频动画。 |
| 184 | Pass | Segoe MDL2 图标在 SoftwareOnly 截图中正常。 |
| 185 | Pass | 所有视觉状态均为 WPF 原生实现。 |
| 186 | Pass | 响应式代码仅调整 Grid 行列，无第二 ViewModel/流水线。 |
| 187 | Pass | 节点数据与延迟逻辑未改。 |
| 188 | Pass | Auto、Direct、链式行为未改。 |
| 189 | Pass | FailureCode、用户结果与恢复行为未改。 |

## 11. Final release visual review

| Line | Status | Result |
|---:|:---:|---|
| 193 | Pass | 已生成 Step 1 ready 1366 × 768。 |
| 194 | Pass | 已生成 Step 2 OpenAI + chain 1366 × 768。 |
| 195 | Pass | 已生成 Step 3 Direct 与 chain。 |
| 196 | Pass | 已生成 Applying、Success、NotObserved、Pre-write failure、Recovered failure、RecoveryFailed。 |
| 197 | Pass | 已生成 125% 与 150% 关键截图。 |
| 198 | Pass | 实现阶段已检查层级、间距、令牌、文案与裁切；Product Design QA 尚未开始。 |
| 199 | N/A | 本轮未执行完整人工键盘走查。 |
| 200 | N/A | 本轮未执行 Windows High Contrast 人工走查。 |
| 201 | Pass | 无新增业务规则或阻塞条件。 |

## Summary

- Pass: 140
- Fail: 3
- N/A: 5
- Product Design QA: Not started（按任务要求停止在下一阶段之前）
