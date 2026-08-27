# AIWorkStation V1 UI Design Specification

## 0. Document control

- Product: AIWorkStation V1
- Platform: Windows 10 / Windows 11 desktop
- UI technology target: WPF, .NET 8, `SoftwareOnly`
- Design direction: Windows 11 Fluent-inspired lightweight commercial utility
- Source evidence: the supplied Step 1, Step 2, and Step 3 screenshots captured on 2026-08-26
- Scope: information architecture, visual design, interaction design, Chinese copy, component rules, DPI behavior, accessibility, and WPF feasibility
- Out of scope: backend behavior, network logic, XAML, production code, additional pages, additional required steps, gates, readiness checks, new confirmations, and new pipelines

This specification preserves the existing four-step flow and all current success, NotObserved, WrongRoute, failure, and recovery semantics.

---

## 1. Design intent

AIWorkStation helps a non-technical Windows user assign a static network exit to selected applications without requiring the user to understand Clash or Mihomo internals.

The interface should always answer four questions in this order:

1. Is my computer ready for this operation?
2. Which applications and static exit am I configuring?
3. What exactly will happen when I apply?
4. Did it work, and if not, is my original configuration safe?

The visual tone is calm, trustworthy, and operational. It should resemble a polished Windows utility—not a developer console, proxy manager, or web administration dashboard.

### Design principles

1. **Outcome first.** Lead with human-readable status; place implementation detail behind “查看技术详情”.
2. **One primary action per screen.** Navigation and destructive or secondary actions remain visually quieter.
3. **Progressive disclosure.** Show chain details only when the resolved connection uses the current Clash node.
4. **No false alarms.** Neutral states are neutral; “尚未验证” must not use success green.
5. **Stable spatial model.** Page title, content, and bottom action bar stay in consistent locations across all four steps.
6. **Color reinforces language.** Every state includes an icon and explicit text; color never carries meaning alone.
7. **Complexity stays inside the product.** Avoid backend terminology in the ordinary interface.

---

## 2. Current experience audit

### Step 1 — 检查电脑

**Health: structurally sound, visually dense.**

Strengths:

- The page exposes all required environment facts and the full node table.
- The node latency note correctly states that latency does not affect application conditions.
- The next action is consistently placed at the lower right.

Issues to resolve:

- Three text columns read like a diagnostic dump rather than a concise readiness summary.
- Labels and values have insufficient visual distinction, slowing scanning.
- The step navigation does not show current versus completed state.
- The node table is visually cramped and leaves a large unused region below because of its fixed maximum height.
- “可用 / 超时 / 测试失败” appears primarily as plain text, making row comparison difficult.
- Current front group and current node are combined in a sentence instead of being distinct facts.

Accessibility risks visible from the screenshot:

- Several secondary blue-gray texts are small and may fall below comfortable contrast at 125% or 150% scaling.
- Some button heights and table rows appear below the recommended 36 DIP interactive target.
- Keyboard focus visibility and horizontal table navigation cannot be verified from a screenshot.

### Step 2 — 配置分流

**Health: functionally complete, highest visual and interaction priority.**

Strengths:

- Application selection and proxy configuration are already separated into two columns.
- OpenAI preset behavior, manual EXE selection, credential caching, validation, and connection mode are all visible in one step.
- The detected front route `FlyintPro → Hongkong 016` is understandable.

Issues to resolve:

- All controls have nearly equal weight, so users cannot tell where to begin.
- The OpenAI preset looks like a generic button instead of the recommended quick choice.
- The empty search table occupies more attention than the selected applications.
- The long DPAPI and temporary-file explanation competes with the task; ordinary users only need a short privacy reassurance.
- The thin full-width blue validation control reads like a divider rather than a primary action.
- “尚未验证” is shown on a green surface, falsely implying success.
- “自动” and the resolved connection mode are separated, which can look contradictory.
- The chain path is represented as technical fields instead of a simple human-readable route.
- Dense vertical stacking makes the right column fragile at 768 px height and high DPI.

Accessibility risks visible from the screenshot:

- Field labels are close to inputs and have weak grouping between sections.
- The small “移除” buttons create narrow targets.
- The connection mode ComboBox hides the three important choices; keyboard operation is possible but discoverability is weak.
- Error placement and focus behavior cannot be verified from the screenshot.

### Step 3 — 确认并应用

**Health: clear but under-informative and spatially empty.**

Strengths:

- The summary answers the core “what will be changed” question.
- “正式应用” is explicit and correctly emphasized.
- The reassurance about backup and recovery is present.

Issues to resolve:

- The summary sits in a very large empty card, weakening confidence rather than creating calm.
- Chain mode does not have a dedicated front-line or route-path row.
- Recovery reassurance is visually separated from the decision point.
- The static exit and connection mode lack compact status icons.
- The confirmation page should make “其他程序不受影响” and recovery behavior equally scannable.

Accessibility risks visible from the screenshot:

- The label/value gap is wide, increasing eye travel.
- The centered bottom status line may be easy to miss.
- Focus order and disabled-state contrast require implementation verification.

### Evidence limits

The supplied screenshots support visual, hierarchy, copy, density, and likely contrast findings. They do not prove keyboard behavior, screen-reader naming, high-contrast compatibility, focus order, live error announcement, or behavior during DPI changes. Those items remain explicit acceptance tests.

---

## 3. Information architecture

### Global shell

1. Window title bar
2. Product header
   - Product name
   - One-line purpose statement
3. Four-step progress navigation
4. Current-step workspace
   - Page header
   - Status banner when needed
   - Main content
   - Sticky bottom action bar

### Step 1 — 检查电脑

1. Page header and environment status
2. Three summary cards
   - 电脑
   - Clash
   - 当前网络
3. Subscription node table
4. Latency-testing note and actions
5. Bottom actions: 重新检查 / 下一步

### Step 2 — 配置分流

1. Page header and status
2. Two task panels
   - 选择软件
   - 配置静态出口
3. Connection mode selector
4. Chain route explanation when applicable
5. Bottom actions: 上一步 / 确认配置

### Step 3 — 确认并应用

1. Page header
2. Compact configuration summary
3. Chain route when applicable
4. Recovery reassurance
5. Bottom actions: 返回修改 / 正式应用

### Step 4 — 结果

1. Result hero
2. Human-readable result message and next action
3. Applied-configuration summary when relevant
4. Recovery result when relevant
5. Collapsed technical details
6. Contextual bottom actions

No fifth step, settings center, account surface, modal sequence, or separate advanced workflow is introduced.

---

## 4. Screen state matrix

| Step | State | Primary content | Primary action | Secondary action | State treatment |
|---|---|---|---|---|---|
| 1 | Checking | Skeleton-like value placeholders and “正在检查…” | Disabled “下一步” | None | Neutral info banner; no error color |
| 1 | Ready | Three populated summaries and node table | 下一步 | 重新检查 | Success icon plus “检查完成” |
| 1 | Environment issue | Facts that were detected plus clear Chinese reason | Recheck when currently supported by behavior | None | Error/warning banner based on existing semantics; no new gate |
| 1 | Testing nodes | Table rows update independently | 下一步 remains governed only by existing logic | 停止测试 | Progress text; latency never shown as eligibility |
| 2 | No apps selected | Recommended OpenAI preset and search/browse options | Confirm configuration remains governed by existing behavior | 上一步 | Neutral empty state |
| 2 | Apps selected, proxy unverified | Selected apps and filled proxy fields | 验证静态出口 | 清除已保存信息 | Neutral “尚未验证” |
| 2 | Testing proxy | Inputs remain visible; validation status updates | Disabled current validation action only while existing command runs | None | Info banner/progress |
| 2 | Direct verified | Actual public IP and resolved Direct mode | 确认配置 | 上一步 | Success panel |
| 2 | Chain selected/resolved | Actual IP if known, front group, current node, route path | 确认配置 | 上一步 | Info/success treatment according to existing validation result |
| 2 | Validation failed | Field-adjacent or section-level Chinese reason | 重新验证 | None | Error banner; no new unsupported condition |
| 3 | Direct confirmation | Apps, exit IP, “直连”, unaffected programs, recovery promise | 正式应用 | 返回修改 | Compact summary card |
| 3 | Chain confirmation | Adds front group, current node, and route path | 正式应用 | 返回修改 | Same page and same action count |
| 4 | Applying | Current stage and low-motion progress | None | None | Info state; window-close behavior stays existing |
| 4 | Success | Completed configuration and actual route facts | 完成 | 继续配置其他软件 / 返回首页 as currently available | Success hero |
| 4 | Success, traffic not observed | Applied status plus explicit non-failure explanation | 完成 | Continue/return action from existing behavior | Info hero, not warning/error |
| 4 | Failed before write | “没有进行修改” plus reason | 返回修改 | 返回首页 where already available | Error hero without recovery alarm |
| 4 | Failed after write, recovered | “原来的网络配置已经恢复” plus reason | 返回配置 | 返回首页 where already available | Warning hero plus green recovery row |
| 4 | Recovery failed | “当前网络状态需要检查” and stop instruction | 返回首页 | 查看技术详情 | Critical error hero; technical details expanded only by user |

---

## 5. Global layout specification

### Window

- Recommended initial size: 1180 × 760 DIP, clamped to the current Windows work area with 24 DIP breathing room.
- Minimum size: 960 × 640 DIP.
- Startup: centered on the active display.
- Default background: `Canvas` token.
- Main content maximum width: 1240 DIP; center when the window is wider.
- Outer margin: 24 DIP at regular width, 16 DIP in compact layout.

### Product header

- Height target: 64–72 DIP.
- Product name: 28/36 SemiBold.
- Purpose line: “为指定 Windows 程序配置独立静态网络出口”.
- Do not place network or diagnostic status in the brand header.

### Step progress

- Four equal segments remain visible at all normal widths.
- Each segment contains step number and short label.
- Current step: primary-tinted background, primary border, primary text.
- Completed step: neutral surface plus check icon and normal text.
- Upcoming step: neutral surface and secondary text.
- Progress styling is navigational feedback only; it must not add click navigation or a new gate.
- At compact width, use labels “检查电脑 / 配置分流 / 确认应用 / 结果”; keep step numbers.

### Workspace surface

- One primary surface per step, 1 DIP border, 12 DIP radius.
- Internal padding: 24 DIP regular, 16 DIP compact.
- Avoid nesting more than one additional card level.
- Page header uses 24/32 SemiBold and a single 14/22 explanatory line.

### Bottom action bar

- Fixed to the bottom row of the current step while the main content scrolls.
- Top separator: 1 DIP border or 16 DIP whitespace, not a shadow.
- Primary action at lower right; secondary action immediately to its left.
- Minimum button size: 88 × 40 DIP. Compact tertiary icon buttons: at least 36 × 36 DIP.

---

## 6. Step 1 high-fidelity specification — 检查电脑

### Header

- Title: “检查电脑”
- Subtitle: “确认 Windows、Clash 和当前网络状态。”
- Ready banner: leading check icon, “检查完成，可以继续配置。”
- Node-test completion is a secondary inline notice below the table, not the top page status.

### Summary cards

Use three equal cards on regular width and a two-plus-one wrap only in compact layout.

#### 电脑

- Icon: PC monitor
- Primary value: Windows edition
- Rows: 系统架构 / 本机时区
- Do not show field names in English.

#### Clash

- Icon: connected nodes or shield-network
- Primary value: “Clash Verge 已运行” or the existing failure wording
- Rows: 版本 / 当前模式 / TUN / 系统代理
- “开 / 关” uses text plus a small status icon; not color alone.

#### 当前网络

- Icon: globe
- Primary value: current subscription
- Rows: 前置策略组 / 当前节点 / 公网 IP / 节点数量
- Keep “系统网络隧道” as a lower-priority row if it remains ordinary-user relevant; otherwise place it in technical details without changing its detection.

### Node table

- Section title: “订阅节点” with count badge, e.g. “59 个”.
- Use a WPF `DataGrid` visual treatment even if the implementation retains the current data source.
- Columns: 节点 / 协议 / 服务器 / 服务器 IP / Fake-IP / 延迟 / 状态 / 测试时间.
- Separate “服务器 IP” and “Fake-IP” visually where feasible; if kept in one field, display a small `Fake-IP` badge after the IP.
- Row height: 34 DIP.
- Header height: 36 DIP.
- Minimum table height: 240 DIP.
- Table fills the available vertical content area; it must not leave a large blank region beneath a fixed-height list.
- Internal vertical scroll remains in the table. Horizontal scroll appears only below 1080 DIP effective content width.
- Status rendering:
  - 可用: check icon + “可用”
  - 超时: clock icon + “超时”
  - 测试失败: warning icon + “测试失败”
  - 测试中: progress icon + “测试中”
  - 未测试: dash icon + “未测试”
- Do not use “合格 / 不合格”.

### Node actions

- Secondary button: “测试全部节点”
- Tertiary button: “停止测试”
- Note: “延迟仅供参考，不影响是否可以应用配置。服务器 IP 也不等于最终静态出口 IP。”
- Current-node provider limitation copy: “当前节点来自 Provider，暂时无法单独测试延迟。”

---

## 7. Step 2 high-fidelity specification — 配置分流

### Header

- Title: “配置分流”
- Subtitle: “选择需要使用静态出口的软件，然后验证代理连接。”
- Cache notice: “已加载本机临时保存的代理信息。”

### Regular layout

- Two columns with a 24 DIP gutter.
- Left: 46% width, “选择软件”.
- Right: 54% width, “配置静态出口”.
- Both columns live inside the same workspace surface; use section separators rather than two oversized cards.

### Left — 选择软件

#### OpenAI preset

- Present as the recommended quick-choice row, not a generic full-width gray button.
- Icon: OpenAI-like application cluster represented by a generic apps icon; do not introduce an external brand asset dependency.
- Title: “OpenAI 应用”
- Description: “ChatGPT 和 Codex 共用一个静态出口”
- Action text: “选择” or selected-state “已选择” according to existing command state.

#### Other applications

- Search field placeholder: “搜索应用名称，例如 Chrome 或 VS Code”
- Search action: “搜索”
- Browse action: “浏览 EXE” with folder icon.
- Results table columns: 应用 / 程序文件 / 来源.
- Empty state: “搜索应用，或从电脑中浏览选择 EXE。”
- Add action: “添加所选应用”.

#### Selected applications

- Section title: “已选择的应用” with count.
- Each row shows display name and executable name; executable path is secondary text or tooltip.
- Remove action: icon plus accessible name “移除 {应用名}”.
- OpenAI preset fallback targets with empty executable paths still display normally; no warning is added.

### Right — 配置静态出口

#### Form arrangement

- Row 1: 协议 (40%) + 端口 (60%).
- Row 2: 服务器, full width.
- Row 3: 用户名（可选）, full width.
- Row 4: 密码（可选）, full width with show/hide affordance only if existing implementation can support it without changing business behavior; otherwise retain PasswordBox without this affordance.
- Field height: 40 DIP.
- Labels sit 6 DIP above inputs.
- Errors appear directly below the related input; section-level network errors appear in the validation panel.

#### Credential cache

- Checkbox: “在本机临时保存代理信息（24 小时）”
- Privacy note: “信息仅保存在当前 Windows 用户下，应用或关闭窗口后不会继续显示密码。”
- Tertiary action: “清除已保存信息”.
- Do not mention DPAPI, candidate files, Script storage, or Mihomo in the ordinary layout. Those details belong in “查看技术详情” or help text.

#### Validation

- Primary section action: “验证静态出口” with globe-check icon.
- The button is a normal 40 DIP control, not a thin bar.
- Neutral state: gray surface + info icon + “尚未验证”.
- Testing state: info surface + “正在验证代理连接和实际出口…”
- Direct success: success surface + “验证成功” + “实际公网出口：{IP}”.
- Chain pending verification: info surface + “将在应用时通过当前 Clash 节点确认静态出口。”
- Failure: error surface + existing human-readable Chinese reason.

### Connection mode

- Section title: “连接方式”.
- Use three horizontally arranged `RadioButton` options styled as a segmented selector:
  - 自动（推荐）
  - 直连
  - 经当前 Clash 节点连接
- At compact width, allow these options to wrap vertically; do not replace or remove an option.
- Auto helper copy:
  - Direct resolution: “自动选择结果：直连”
  - Chain resolution: “自动选择结果：经当前 Clash 节点连接”
- Manual selection helper copy:
  - Direct: “将固定使用直连方式。”
  - Chain: “将固定通过当前 Clash 节点连接。”
- Do not show `DialerProxy` or `dialer-proxy`.

### Chain route panel

Show when the manual selection is chain mode or Auto currently resolves to chain mode.

- Header: “当前网络路径”
- Summary rows:
  - 前置策略组：FlyintPro
  - 当前前置节点：Hongkong 016
- Route line:
  - `ChatGPT / Codex → AI 静态网络 → FlyintPro → Hongkong 016 → 静态住宅出口`
- Use small directional chevrons from the icon library, not text-art arrows in implementation.
- This panel is informational and never introduces eligibility or a blocking condition.

---

## 8. Step 3 high-fidelity specification — 确认并应用

### Header

- Title: “确认并应用”
- Subtitle: “确认下面的信息，然后应用到当前 Clash 配置。”

### Configuration summary

Use one compact summary card with 16 DIP row spacing. Maximum content width: 760 DIP.

Rows:

1. 目标软件 — `ChatGPT (ChatGPT.exe) + Codex (codex.exe)`
2. 静态出口 — `203.0.113.24（示例）` or the existing actual-exit representation
3. 连接方式 — `直连` or `经当前 Clash 节点连接`
4. 前置策略组 — shown only for chain mode
5. 当前前置节点 — shown only for chain mode
6. 其他程序 — `保持当前网络，不受影响`

For chain mode, add the same “当前网络路径” row used in Step 2.

### Recovery reassurance

Place immediately below the summary in an info panel:

- Title: “应用过程会保护当前配置”
- Body: “应用前会保存当前设置；如果写入后验证失败，AIWorkStation 会尝试恢复原来的网络配置。”

Do not expose candidate, hash, authorization, runtime, or receipt terminology.

### Actions

- Secondary: “返回修改”
- Primary: “正式应用”
- While applying, do not allow duplicate activation; keep the existing behavior.

---

## 9. Step 4 high-fidelity specification — 结果

### Shared layout

- Centered result column, maximum width 720 DIP.
- Result icon: 40 DIP.
- Result title: 28/36 SemiBold.
- Message: 16/24.
- Configuration summary appears only when it helps the user understand the outcome.
- “查看技术详情” is an `Expander`, collapsed by default except recovery failure may visually emphasize it without automatically exposing stack traces.

### Applying

- Title: “正在应用配置”
- Supporting copy: “请保持 Clash Verge 运行，完成前不要关闭此窗口。”
- Stage list, reusing existing pipeline status:
  1. 正在检查当前配置
  2. 正在生成分流配置
  3. 正在验证网络
  4. 正在保存配置
  5. 正在重新加载 Clash
  6. 正在确认程序分流
- Use a standard low-motion progress indicator. Do not use decorative animation.

### Success

- Title: “配置完成”
- Message: “所选应用现在会使用指定的静态网络出口。”
- Show selected apps, connection mode, actual public exit, and current front route when chain mode is active.
- Primary action: “完成”.
- Existing continuation action may use “继续配置其他软件”.

### Success, traffic not observed

- Title: “配置已应用”
- Message: “暂时没有检测到目标软件的新网络请求，这不代表配置失败。”
- Suggested action: “打开目标软件并正常使用，AIWorkStation 可以在下次检查时确认实际分流状态。”
- Use info blue, not warning yellow or error red.

### Failure before write

- Title: “没有进行修改”
- Message: the existing specific Chinese reason.
- Supporting copy: “当前网络配置没有被更改。”
- Primary action: “返回修改”.

### Failure after write, recovery succeeded

- Title: “配置没有完成”
- Message: the existing specific Chinese reason.
- Recovery row: check icon + “原来的网络配置已经恢复。”
- Primary action: “返回配置”.

For post-write exit timeout, use exactly:

- “应用后的静态出口验证超时，原配置已经恢复。”

Do not display “Clash 配置被其他程序修改” unless the existing result is a true TargetChanged case.

### Recovery failed

- Title: “当前网络状态需要检查”
- Message: “无法确认原来的网络配置已经完整恢复，请暂时不要继续应用配置。”
- Suggested action: “返回首页并手动检查 Clash Verge；需要排查时再展开技术详情。”
- Primary action: “返回首页”.
- Secondary: “查看技术详情”.
- Use error icon and text; do not rely on a red card alone.

---

## 10. Component inventory

| Component | Purpose | Key variants |
|---|---|---|
| AppShell | Brand, four-step progress, workspace | Regular / compact |
| StepProgress | Shows current, complete, upcoming steps | Current / completed / upcoming |
| PageHeader | Step title and one-line purpose | With/without status banner |
| StatusBanner | Human-readable state | Info / success / warning / error |
| SummaryCard | Groups 3–6 key facts | Standard / compact |
| KeyValueRow | Scannable label and value | Normal / emphasized / optional |
| StatusBadge | Table and compact state | Available / timeout / failed / testing / neutral |
| NodeTable | Node and latency information | Empty / populated / testing |
| FormField | Label, input, helper, error | Default / focused / disabled / error |
| SensitiveField | Password input | Default / error |
| ApplicationPreset | Recommended OpenAI selection | Available / selected |
| ApplicationResultTable | Search results | Empty / populated |
| SelectedApplicationRow | Selected target and removal | Normal |
| ConnectionModeSelector | Auto / Direct / current Clash node | Selected / unselected / focus |
| RoutePath | Human-readable chain | Direct hidden / chain visible |
| ValidationPanel | Proxy test result | Neutral / testing / success / error |
| ConfirmationSummary | Final review | Direct / chain |
| RecoveryReassurance | Explains protection before apply | Info |
| ResultHero | Outcome and next action | Applying / success / NotObserved / failure / recovered / recovery failed |
| TechnicalDetails | Diagnostic detail | Collapsed / expanded |
| ActionBar | Stable primary and secondary actions | Standard / applying |
| EmptyState | No results or selection | Search / selected apps / nodes |
| InlineError | Field or section error | Error |
| Tooltip | Short clarification | Keyboard and pointer accessible |

---

## 11. Design tokens

### Color

| Token | Value | Use |
|---|---|---|
| Canvas | `#F5F7FA` | Window background |
| Surface | `#FFFFFF` | Primary workspace and cards |
| SurfaceSubtle | `#F8FAFC` | Table headers, compact summaries |
| Border | `#DCE3EA` | Default 1 DIP border |
| BorderStrong | `#C7D0DB` | Focus-adjacent or emphasized separators |
| TextPrimary | `#172033` | Titles and body text |
| TextSecondary | `#58677C` | Supporting copy |
| TextTertiary | `#748196` | Metadata; never essential information alone |
| Primary | `#2563EB` | Primary action and current step |
| PrimaryHover | `#1D4ED8` | Pointer hover |
| PrimaryPressed | `#1E40AF` | Pressed state |
| PrimarySubtle | `#EFF6FF` | Current step and info background |
| Focus | `#2563EB` | 2 DIP keyboard focus outline |
| SuccessText | `#166534` | Success icon/text |
| SuccessSurface | `#F0FDF4` | Success panel |
| SuccessBorder | `#BBF7D0` | Success border |
| WarningText | `#92400E` | Warning icon/text |
| WarningSurface | `#FFFBEB` | Warning panel |
| WarningBorder | `#FDE68A` | Warning border |
| ErrorText | `#B42318` | Error icon/text |
| ErrorSurface | `#FEF3F2` | Error panel |
| ErrorBorder | `#FECDCA` | Error border |
| DisabledSurface | `#F1F3F5` | Disabled controls |
| DisabledText | `#8A95A5` | Disabled label; pair with disabled shape |

Rules:

- Do not use large high-saturation fills.
- Primary blue is reserved for current navigation and primary actions.
- Neutral “尚未验证” uses `SurfaceSubtle`, not `SuccessSurface`.
- In Windows High Contrast, system colors override custom status colors; border and text remain visible.

### Typography

Font family priority: Segoe UI, Microsoft YaHei UI, system sans-serif fallback.

| Token | Size / line height | Weight | Use |
|---|---|---|---|
| Display | 28 / 36 | SemiBold | Product name, result title |
| Heading1 | 24 / 32 | SemiBold | Page title |
| Heading2 | 18 / 26 | SemiBold | Main sections |
| Heading3 | 15 / 22 | SemiBold | Card title |
| Body | 14 / 22 | Regular | Main UI text |
| BodyStrong | 14 / 22 | SemiBold | Labels and emphasized values |
| Data | 13 / 20 | Regular | Table rows and technical values |
| Caption | 12 / 18 | Regular | Helper text and metadata |
| Technical | 12 / 18 | Regular monospace | Expanded technical details only |

Avoid all-caps English headings. Chinese body text must not be smaller than 12 DIP.

### Spacing

- Base unit: 4 DIP.
- Scale: 4, 8, 12, 16, 20, 24, 32, 40.
- Section-to-section: 24 DIP.
- Label-to-control: 6–8 DIP.
- Control-to-helper: 6 DIP.
- Card padding: 16 DIP compact, 20 DIP regular.
- Workspace padding: 16 DIP compact, 24 DIP regular.

### Radius

- Input and button: 4 DIP.
- Segmented option and status panel: 6 DIP.
- Card: 8 DIP.
- Main workspace: 12 DIP.
- Avoid pill shapes except small count/status badges.

### Border

- Default: 1 DIP solid `Border`.
- Focus: 2 DIP `Focus` outline with at least 1 DIP visual separation from the control border.
- No complex shadows. If separation is required, use border plus background contrast.

### Control heights

- Primary and secondary button: 40 DIP.
- Standard input and ComboBox: 40 DIP.
- Compact table action: 36 DIP.
- Checkbox line: minimum 36 DIP click row.
- Data row: 34 DIP.
- Segmented connection option: minimum 40 DIP.

---

## 12. Interaction rules

1. Tab order follows visual reading order: page status → main content left-to-right/top-to-bottom → bottom actions.
2. Enter activates the page’s primary action only when that action is already enabled by existing logic.
3. Escape does not dismiss the window or abandon an active apply operation.
4. Every pointer hover state has an equivalent keyboard focus state.
5. Focus remains on the initiating control after a non-navigational operation; on validation error, move focus to the first invalid field only if current WPF behavior supports it without business changes.
6. Status changes use text and an icon, and are exposed through an appropriate WPF automation live region when feasible.
7. Connection mode choices remain visible; selecting a mode updates the helper summary immediately.
8. Chain details appear or disappear in the same page without animation or layout jump greater than one section.
9. Proxy validation never changes a manually selected connection mode in the UI.
10. Application removal is immediate and does not add a confirmation dialog.
11. Clearing cached credentials uses the existing action and existing semantics; do not add a new mandatory confirmation.
12. Node latency testing is cancellable and remains observational.
13. During Apply, show the current existing pipeline stage and prevent duplicate application using current behavior.
14. Technical details remain collapsed by default and never expose secrets, passwords, authorization headers, or full credentials.
15. Tooltips supplement visible labels; they never contain required instructions that are unavailable by keyboard.

---

## 13. Exact Chinese copy

### Global

- Product purpose: “为指定 Windows 程序配置独立静态网络出口”
- Steps: “1 检查电脑” / “2 配置分流” / “3 确认并应用” / “4 结果”

### Step 1

- Title: “检查电脑”
- Subtitle: “确认 Windows、Clash 和当前网络状态。”
- Checking: “正在检查电脑和当前网络…”
- Ready: “检查完成，可以继续配置。”
- Cards: “电脑” / “Clash” / “当前网络”
- Facts: “系统架构” / “本机时区” / “版本” / “当前模式” / “TUN” / “系统代理” / “当前订阅” / “前置策略组” / “当前节点” / “公网 IP” / “节点数量”
- Table title: “订阅节点”
- Table columns: “节点” / “协议” / “服务器” / “服务器 IP / Fake-IP” / “延迟” / “状态” / “测试时间”
- Actions: “测试全部节点” / “停止测试” / “重新检查” / “下一步”
- Note: “延迟仅供参考，不影响是否可以应用配置。服务器 IP 也不等于最终静态出口 IP。”

### Step 2

- Title: “配置分流”
- Subtitle: “选择需要使用静态出口的软件，然后验证代理连接。”
- Cache notice: “已加载本机临时保存的代理信息。”
- Sections: “选择软件” / “配置静态出口” / “已选择的应用” / “连接方式” / “当前网络路径”
- OpenAI title: “OpenAI 应用”
- OpenAI description: “ChatGPT 和 Codex 共用一个静态出口”
- Search placeholder: “搜索应用名称，例如 Chrome 或 VS Code”
- Actions: “搜索” / “浏览 EXE” / “添加所选应用” / “移除” / “清除已保存信息” / “验证静态出口” / “上一步” / “确认配置”
- Fields: “协议” / “服务器” / “端口” / “用户名（可选）” / “密码（可选）”
- Cache checkbox: “在本机临时保存代理信息（24 小时）”
- Privacy note: “信息仅保存在当前 Windows 用户下，应用或关闭窗口后不会继续显示密码。”
- Neutral validation: “尚未验证”
- Testing: “正在验证代理连接和实际出口…”
- Success: “验证成功” / “实际公网出口：{IP}”
- Chain pending: “将在应用时通过当前 Clash 节点确认静态出口。”
- Modes: “自动（推荐）” / “直连” / “经当前 Clash 节点连接”
- Auto result: “自动选择结果：{直连 / 经当前 Clash 节点连接}”
- Manual Direct: “将固定使用直连方式。”
- Manual chain: “将固定通过当前 Clash 节点连接。”
- Chain facts: “前置策略组：{名称}” / “当前前置节点：{名称}”

### Step 3

- Title: “确认并应用”
- Subtitle: “确认下面的信息，然后应用到当前 Clash 配置。”
- Rows: “目标软件” / “静态出口” / “连接方式” / “前置策略组” / “当前前置节点” / “其他程序”
- Other apps: “保持当前网络，不受影响”
- Recovery title: “应用过程会保护当前配置”
- Recovery body: “应用前会保存当前设置；如果写入后验证失败，AIWorkStation 会尝试恢复原来的网络配置。”
- Actions: “返回修改” / “正式应用”

### Step 4

- Applying title: “正在应用配置”
- Applying body: “请保持 Clash Verge 运行，完成前不要关闭此窗口。”
- Stages: “正在检查当前配置” / “正在生成分流配置” / “正在验证网络” / “正在保存配置” / “正在重新加载 Clash” / “正在确认程序分流”
- Success title: “配置完成”
- Success body: “所选应用现在会使用指定的静态网络出口。”
- NotObserved title: “配置已应用”
- NotObserved body: “暂时没有检测到目标软件的新网络请求，这不代表配置失败。”
- NotObserved action: “打开目标软件并正常使用，AIWorkStation 可以在下次检查时确认实际分流状态。”
- Pre-write failure: “没有进行修改” / “当前网络配置没有被更改。”
- Recovered failure: “配置没有完成” / “原来的网络配置已经恢复。”
- Post-write timeout: “应用后的静态出口验证超时，原配置已经恢复。”
- Recovery failure title: “当前网络状态需要检查”
- Recovery failure body: “无法确认原来的网络配置已经完整恢复，请暂时不要继续应用配置。”
- Recovery failure action: “返回首页并手动检查 Clash Verge；需要排查时再展开技术详情。”
- Actions: “完成” / “继续配置其他软件” / “返回修改” / “返回配置” / “返回首页” / “查看技术详情”

---

## 14. DPI and small-screen rules

1. All measurements are WPF device-independent pixels.
2. At effective content width ≥1080 DIP, Step 2 uses two columns.
3. Below 1080 DIP, Step 2 becomes a single vertical flow: selected applications first, static exit second, connection mode third. This is layout-only and adds no step.
4. Below 1024 DIP, summary cards may wrap; each card remains at least 280 DIP wide.
5. At 1366 × 768 and 100%, maximize or clamp the initial window to the work area; keep the bottom action bar visible while the content region scrolls.
6. At 1440 × 900 and above, use the 1180 × 760 DIP default window.
7. At 125% and 150%, never scale custom pixel assets; icons use vector/font resources and text follows system DPI.
8. The entire step workspace may scroll vertically on compact screens. The node table retains its own vertical scroll and a minimum 240 DIP visible height.
9. Step 2 must have an outer vertical ScrollViewer in compact/high-DPI layouts so proxy fields and bottom actions are never clipped.
10. Bottom actions remain in a separate auto-height row whenever the viewport allows; on extremely short work areas they join the outer scroll after the content.
11. Labels do not wrap unless longer than 24 Chinese characters. Helper, status, and error copy wraps at word boundaries and never clips.
12. Buttons keep their full Chinese labels; do not collapse primary actions into icon-only controls.
13. The step progress bar remains four segments until below 900 DIP; at that point labels shorten but all four steps remain visible.

---

## 15. Accessibility specification

1. Target WCAG-style contrast: 4.5:1 for normal text, 3:1 for large text and essential non-text boundaries.
2. Do not use color alone for success, warning, error, testing, or current-step state.
3. Every input has a visible label and a unique automation name.
4. Every icon-only action has an accessible name and tooltip.
5. Keyboard focus uses a visible 2 DIP outline that remains visible in light and high-contrast themes.
6. Tab order matches the screen’s visual order; hidden or collapsed controls are excluded.
7. Error text is placed next to the relevant field and associated through WPF automation help text where feasible.
8. Status banners and apply-stage text should be announced when they change, without repeatedly stealing keyboard focus.
9. The node table supports keyboard row navigation; status and latency values are readable as text.
10. Remove actions include the application name in their automation label.
11. High Contrast uses system text, highlight, window, and border colors rather than hardcoded light surfaces where possible.
12. Avoid animations beyond standard low-motion progress feedback; information remains understandable when animation is disabled.

---

## 16. WPF feasibility notes

- Use ordinary `Grid`, `Border`, `TextBlock`, `Button`, `TextBox`, `PasswordBox`, `ComboBox`, `RadioButton`, `DataGrid`, `Expander`, and `ProgressBar` controls.
- Implement hierarchy through spacing, border, fill, and typography; do not depend on shadow or blur.
- The connection selector can be three standard RadioButtons with a shared item style; its semantic behavior remains the existing enum selection.
- Responsive behavior can use width-aware visual states, triggers, or a small view-only layout adapter. It must not create an alternate business pipeline.
- Use `ScrollViewer` only at clear containment boundaries. Avoid nested unconstrained vertical ScrollViewers around the node table.
- Use a DataGrid or the existing ListView/GridView with equivalent row and header styling. Do not change the node data source or latency behavior.
- Prefer `Segoe Fluent Icons` glyphs where available with a stable fallback. Do not use emoji as product icons.
- Ensure icon glyphs render under `SoftwareOnly`; avoid effects, opacity masks with animation, and complex geometry.
- Use dynamic resources or system-color fallbacks for High Contrast.
- Default technical details use a plain bordered surface and monospace text; no terminal-like full-page treatment.
- Password and credential content must never be repeated in summaries, tooltips, validation messages, logs, or technical details.

---

## 17. Required icon list

Use common Windows linear icons, 16–20 DIP stroke/glyph size unless noted.

- Computer / monitor
- Windows or device information
- Network / globe
- Clash or connected nodes (generic, not a new brand asset)
- Shield / protected configuration
- Check circle
- Information circle
- Warning triangle
- Error circle
- Clock / timeout
- Refresh
- Search
- Folder open / browse EXE
- Applications grid
- Add
- Remove / trash
- Lock / credentials
- Clear saved information
- Test connection / globe check
- Direct connection / arrow
- Chain route / branching path
- Chevron right for route path
- Chevron down/up for Expander
- Play/apply
- Home
- Technical details / document-code

Icons never replace the visible labels of primary actions.

---

## 18. Handoff boundary

This document is a design specification only. Implementation must wait for product-owner approval. When implementation begins, it must preserve:

- the four existing steps;
- the existing ViewModel commands and business states;
- current Direct, DialerProxy, application discovery, credential cache, node latency, apply, verify, and recovery behavior;
- no new required input, confirmation, eligibility check, readiness check, gate, or pipeline.
