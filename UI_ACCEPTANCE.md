# AIWorkStation V1 UI Acceptance Checklist

Use this checklist after the Product Owner approves `DESIGN.md` and UI implementation begins. Every item is pass/fail. A visual pass does not replace keyboard, DPI, High Contrast, or state verification.

## 1. Scope protection

- [ ] Exactly four steps remain: 检查电脑 / 配置分流 / 确认并应用 / 结果.
- [ ] No fifth page, settings center, account flow, or new modal sequence exists.
- [ ] No new required field, confirmation, gate, readiness rule, eligibility rule, unsupported condition, or pipeline was added.
- [ ] Backend success, NotObserved, WrongRoute, failure, and recovery semantics are unchanged.
- [ ] Direct, DialerProxy, Application Discovery, Credential Cache, and node latency behavior are unchanged.
- [ ] Ordinary UI does not expose Mihomo Controller, Runtime Candidate, Runtime Baseline, Hash, SHA-256, Transaction Marker, Journal, Gate, Readiness, Authorization, Fingerprint, `dialer-proxy`, Managed Proxy Definition, Recovery Semantic Equality, Controller PUT, or stack traces.
- [ ] Technical details remain collapsed by default.
- [ ] No production screen displays proxy passwords, tokens, credentials, or secrets after input.

## 2. Global shell

- [ ] Product title and one-line purpose are visible without dominating the workspace.
- [ ] The four-step progress navigation clearly distinguishes current, completed, and upcoming steps.
- [ ] Progress state is conveyed by text/shape/icon as well as color.
- [ ] Each screen has one visually dominant primary action.
- [ ] Primary and secondary actions remain in a consistent bottom-right action bar.
- [ ] Main workspace uses one primary surface with no more than one nested card level.
- [ ] Borders are 1 DIP, radii are restrained, and no complex shadows, blur, Mica, Acrylic, transparency, or GPU effects are used.

## 3. Step 1 — 检查电脑

- [ ] Page title is “检查电脑”.
- [ ] Subtitle is “确认 Windows、Clash 和当前网络状态。”.
- [ ] Ready state reads “检查完成，可以继续配置。”.
- [ ] Separate summary areas show Windows version, architecture, timezone, Clash status/version/mode, TUN, system proxy, subscription, front group, current node, public IP, and node count.
- [ ] Front group and current node are separate facts.
- [ ] Node table includes node, protocol, server, server IP/Fake-IP, latency, state, and test time.
- [ ] Fake-IP is visually distinguishable without implying an error.
- [ ] Node row state uses icon plus text, not color alone.
- [ ] No row is labeled “合格” or “不合格”.
- [ ] “测试全部节点” and “停止测试” remain available according to existing behavior.
- [ ] The note explicitly says latency does not affect whether configuration can be applied.
- [ ] Node table has at least 240 DIP visible height and uses available vertical space.
- [ ] The page does not leave a large empty area below a fixed-height node table at 1440 × 900.
- [ ] “重新检查” is secondary and “下一步” is primary.

## 4. Step 2 — 配置分流

- [ ] Page title is “配置分流”.
- [ ] Subtitle is “选择需要使用静态出口的软件，然后验证代理连接。”.
- [ ] Regular layout clearly separates “选择软件” and “配置静态出口”.
- [ ] OpenAI preset is recognizable as the recommended quick selection.
- [ ] OpenAI preset description states “ChatGPT 和 Codex 共用一个静态出口”.
- [ ] Selecting the OpenAI preset shows both ChatGPT and Codex even when Codex is not running.
- [ ] Search, browse EXE, result selection, selected applications, and removal remain available.
- [ ] Selected applications show display name and executable name.
- [ ] Proxy fields include protocol, server, port, optional username, and optional password.
- [ ] Proxy fields have visible labels and field-adjacent errors.
- [ ] Cache checkbox reads “在本机临时保存代理信息（24 小时）”.
- [ ] Ordinary privacy copy does not mention DPAPI, Script, candidate files, or Mihomo.
- [ ] “清除已保存信息” remains a tertiary action.
- [ ] “验证静态出口” renders as a normal 40 DIP button, not a thin colored bar.
- [ ] “尚未验证” uses a neutral surface, not green.
- [ ] Validation success includes the actual public exit IP.
- [ ] Connection mode visibly offers all three choices: 自动（推荐） / 直连 / 经当前 Clash 节点连接.
- [ ] The resolved result of Auto is shown in plain Chinese.
- [ ] A manual Direct or chain choice is never visually overwritten by a direct test result.
- [ ] Chain mode shows front policy group and current front node.
- [ ] Chain mode shows a human-readable network path.
- [ ] Ordinary UI never displays `DialerProxy` or `dialer-proxy`.
- [ ] Chain information is informational and does not add a new blocking state.
- [ ] “上一步” is secondary and “确认配置” is primary.

## 5. Step 3 — 确认并应用

- [ ] Page title is “确认并应用”.
- [ ] Summary displays selected applications, actual static exit, connection mode, and unaffected-program statement.
- [ ] Chain mode additionally displays front group, current node, and network path.
- [ ] Direct mode does not show irrelevant blank chain rows.
- [ ] Other-program copy reads “保持当前网络，不受影响”.
- [ ] Recovery reassurance is adjacent to the configuration summary.
- [ ] Recovery reassurance states that current settings are saved and recovery is attempted after a write-time verification failure.
- [ ] The page does not display Gate, Readiness, Trial Receipt, Runtime Candidate, Hash, Journal, deployment authorization, or other developer terminology.
- [ ] “返回修改” is secondary and “正式应用” is the single primary action.
- [ ] Summary content is compact and does not sit inside an unnecessarily large empty card.

## 6. Step 4 — applying and result states

### Applying

- [ ] Title reads “正在应用配置”.
- [ ] Current progress text is visible and corresponds to the existing pipeline stage.
- [ ] Supported stage copy includes check, build, network validation, save, Clash reload, and route confirmation.
- [ ] Progress uses low-motion standard controls; there is no decorative animation.
- [ ] Duplicate Apply activation remains prevented by existing behavior.

### Success

- [ ] Title reads “配置完成”.
- [ ] Selected apps, connection mode, actual public exit, and chain route when applicable are shown.
- [ ] Primary completion action has a specific label such as “完成”.

### Success with traffic not observed

- [ ] Title reads “配置已应用”.
- [ ] Copy says no new target-app traffic was observed.
- [ ] Copy explicitly says this does not mean configuration failed.
- [ ] The user is told to open and normally use the target application.
- [ ] State uses info treatment, not error or warning treatment.

### Failure before write

- [ ] Title reads “没有进行修改”.
- [ ] A specific Chinese reason is shown.
- [ ] Copy confirms the current network configuration was not changed.
- [ ] “返回修改” is available.

### Failure after write with successful recovery

- [ ] Title reads “配置没有完成”.
- [ ] Recovery row reads “原来的网络配置已经恢复。”.
- [ ] A specific Chinese failure reason is shown.
- [ ] Post-write exit timeout reads “应用后的静态出口验证超时，原配置已经恢复。”.
- [ ] Timeout does not display “Clash 配置被其他程序修改”.
- [ ] “返回配置” is available.

### Recovery failure

- [ ] Title reads “当前网络状态需要检查”.
- [ ] Copy tells the user not to continue applying configuration.
- [ ] “返回首页” is available.
- [ ] “查看技术详情” is available.
- [ ] Error meaning is shown through icon and text, not red alone.

## 7. Design tokens and visual quality

- [ ] Segoe UI / Microsoft YaHei UI-compatible typography is used.
- [ ] Product/result display text is 28/36 SemiBold.
- [ ] Page titles are 24/32 SemiBold.
- [ ] Main body text is 14/22 and no essential Chinese text is smaller than 12 DIP.
- [ ] Primary color is close to `#2563EB` and reserved for current navigation and primary actions.
- [ ] Window canvas, surface, border, primary text, and secondary text match the approved token roles in `DESIGN.md`.
- [ ] Neutral, success, warning, and error states use distinct icon/text/border combinations.
- [ ] Controls are at least 40 DIP high unless explicitly specified as compact.
- [ ] Compact interactive controls are at least 36 × 36 DIP.
- [ ] Spacing follows the 4 DIP base scale.
- [ ] Card radius does not exceed 8 DIP; workspace radius does not exceed 12 DIP.
- [ ] No exaggerated gradient, large saturated panel, game-like treatment, or web-dashboard visual pattern is present.

## 8. Keyboard and accessibility

- [ ] All interactive controls are reachable by keyboard.
- [ ] Tab order follows visual reading order.
- [ ] Keyboard focus is clearly visible with at least a 2 DIP outline.
- [ ] Every field has a unique visible label and automation name.
- [ ] Icon-only actions have accessible names and tooltips.
- [ ] Remove-action automation names include the application name.
- [ ] Dynamic validation and apply-status updates are announced without stealing focus repeatedly.
- [ ] Error messages are adjacent and associated with their relevant input or section.
- [ ] Status meaning never depends on color alone.
- [ ] Normal text reaches a 4.5:1 contrast target; large text and essential boundaries reach 3:1.
- [ ] Node table supports keyboard row navigation.
- [ ] Technical details can be expanded and read by keyboard.
- [ ] Windows High Contrast keeps text, boundaries, focus, and primary actions readable.
- [ ] No required information exists only in a pointer tooltip.

## 9. Window, DPI, and small-screen checks

- [ ] Recommended initial window is approximately 1180 × 760 DIP and clamps to the work area.
- [ ] Minimum window is no larger than 960 × 640 DIP.
- [ ] At 1366 × 768 / 100%, all four steps are usable and bottom actions remain reachable.
- [ ] At 1440 × 900 / 100%, no major content area has excessive empty space.
- [ ] At 1920 × 1080 / 100%, content remains centered with a reasonable maximum width.
- [ ] At 125% DPI, all labels, values, fields, and actions remain visible or reachable through intended scrolling.
- [ ] At 150% DPI, Step 2 reflows without clipping proxy fields or connection choices.
- [ ] Below 1080 DIP effective content width, Step 2 becomes a single vertical flow without adding a page.
- [ ] Step progress keeps all four steps visible; compact labels may be used below 900 DIP.
- [ ] Node table keeps at least 240 DIP visible height where the work area permits.
- [ ] Long status, helper, error, application, node, and route text wraps instead of clipping.
- [ ] Primary button labels are never replaced by icon-only controls.
- [ ] Horizontal scrolling is confined to the node table when necessary.
- [ ] Nested vertical scrolling does not trap mouse wheel or keyboard focus.

## 10. WPF and SoftwareOnly feasibility

- [ ] Implementation uses stable WPF controls such as Grid, Border, TextBlock, Button, TextBox, PasswordBox, ComboBox, RadioButton, DataGrid/ListView, Expander, and ProgressBar.
- [ ] No Mica, Acrylic, background blur, transparent window, complex shadow, GPU shader, or high-frequency animation is used.
- [ ] Icons render correctly under `SoftwareOnly`.
- [ ] Visual states do not require web-only behavior.
- [ ] Responsive layout changes do not create a second ViewModel flow or business pipeline.
- [ ] Node table redesign does not change its data, latency meaning, or testing logic.
- [ ] Connection selector redesign does not change Auto, Direct, or chain behavior.
- [ ] Result redesign does not change failure codes, user outcomes, or recovery behavior.

## 11. Final release visual review

- [ ] Capture Step 1 ready state at 1366 × 768 / 100%.
- [ ] Capture Step 2 with OpenAI preset selected and chain mode at 1366 × 768 / 100%.
- [ ] Capture Step 3 Direct and chain confirmations.
- [ ] Capture all six Step 4 states using deterministic test data.
- [ ] Repeat key screens at 125% and 150% DPI.
- [ ] Compare screenshots against `DESIGN.md` for hierarchy, spacing, token, copy, and clipping.
- [ ] Perform a keyboard-only pass from launch through confirmation without activating real Apply.
- [ ] Perform a Windows High Contrast pass.
- [ ] Confirm no new business rule or blocking condition appeared in the UI implementation diff.

