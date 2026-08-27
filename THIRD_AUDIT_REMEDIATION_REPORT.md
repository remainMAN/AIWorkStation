# AIWorkStationClean Third Audit Remediation Report

## Audit scope

- Project: `AIWorkStationClean`
- Source: `D:\AIWorkStationClean`
- Stage: Second Source Audit Remediation / Third Audit Package
- Remediation completed: 2026-08-23
- Development status after verification: Frozen
- Explicitly out of scope and never used as a source of truth: `D:\AIWorkStation`, `D:\AIWorkStationV1`

The product still has one formal deployment pipeline:

`Check -> Build -> Validate -> Backup -> Write -> Reload -> Verify -> Recover`

No second pipeline, Gate framework, Receipt, DeploymentReadiness, Planner, reconciliation layer, new state machine, or background transport switching framework was added.

## Baseline recorded before remediation

| Metric | Baseline |
|---|---:|
| Build warnings | 0 |
| Build errors | 0 |
| Tests passed | 60 |
| Tests failed | 0 |
| Tests skipped | 0 |
| Tests total | 60 |
| Production source files | 40 |
| Test source files | 7 |
| Production LOC | 3308 |
| Test LOC | 982 |

## P1 remediation results

### P1-1 Dialer actual exit IP — Passed

- A Dialer candidate is loaded only in the temporary Mihomo Runtime.
- `AI静态链` is explicitly selected to `AI静态出口-链式`.
- The public IP request is sent through an existing local Mihomo HTTP/SOCKS/mixed inbound, using replaceable IP providers.
- Delay-only success is not accepted as actual-exit verification.
- Known expected IP values are compared with the actual result; unknown expected IP values are reported without claiming a match.
- The actual Dialer exit IP is returned to the UI result.

### P1-2 Transport stability selection — Passed

- Direct mode uses three independent samples.
- Authentication failure stops immediately and never falls back to Dialer.
- Three stable, identical-IP successes select Direct; two identical-IP successes are usable with a warning; fewer successes are treated as network instability.
- Only connection failure or timeout can trigger Direct-to-Dialer fallback, and the fallback happens before any persistent write.
- The selected transport is fixed for the remainder of that Apply operation.
- SOCKS authentication byte buffers are cleared immediately after use.

### P1-3 Temporary Runtime restore — Passed

- A small pre-load baseline records profile UID, Extension existence/hash, canonical `/configs` state, managed objects, group selection/members, and managed rules.
- Runtime YAML is re-read immediately before baseline capture and guarded by SHA-256 checks before and after capture to close the stale-YAML window.
- The original Runtime YAML is restored after both success and failure, then `/configs`, `/proxies`, `/rules`, profile UID, and Extension hash are read again.
- A failed or unverifiable restore returns `RecoveryFailed`, sets recovery flags truthfully, performs zero persistent writes, and disables immediate retry until environment recheck.

### P1-4 Recovery semantic equivalence — Passed

- Backup manifest semantics include current profile UID, Script binding, Extension path/hash, managed proxy names and definition hashes, managed group state/selection/members, managed rules, and canonical `/configs` SHA-256.
- Managed proxy definition hashes cover the effective YAML definition without storing proxy credential values in the manifest.
- Recovery restores files, reloads Clash, actively restores the previous `AI静态链` selection, and then verifies the semantic baseline.
- JSON/YAML property order is normalized for semantic comparison.
- Marker deletion happens before best-effort backup cleanup, so a live marker never intentionally points to a deleted backup workspace.

### P1-5 Transport persistence — Passed

- Direct mode writes Direct first in `AI静态链`.
- Dialer mode writes Dialer first in `AI静态链`.
- Environments without a safe front group contain Direct only.
- Reload is followed by an explicit Controller selection of the final transport.
- User global `store-selected` configuration is observed only; it is not modified.

### P1-6 Strict Script ownership — Passed

- Ownership requires the exact leading headers `// AIWORKSTATION MANAGED` and `// VERSION: 1`.
- UTF-8 BOM and LF/CRLF are handled explicitly.
- The entire generated structure, one `main`, managed objects, and terminal structure must match.
- A marker in the middle, unknown appended logic, a wrong version, or an altered structure is rejected as user logic and is not overwritten.

### P1-7 Candidate credential cleanup — Passed

- Mihomo validation candidates use a current-user-only directory and file ACL, a random `candidate-*.yaml` name, exclusive creation, WriteThrough, disk flush, and `finally` deletion.
- Startup removes candidate files older than one hour.
- Candidate diagnostics are sanitized and truncated; complete YAML is not logged or displayed.
- Temporary UI credential storage uses DPAPI `CurrentUser`; final Script credential persistence is described truthfully as required by Mihomo.
- ViewModel and PasswordBox plaintext are cleared after Apply, return-home, and window close.

### P1-8 ApplyEngine integration tests — Passed

Tests call the production `ApplyEngine.ApplyAsync` and cover:

- temporary Runtime recovery failure and retry blocking;
- Direct network failure rebuilding to Dialer before write;
- post-write verification failure restoring files and Runtime semantics;
- pre-write failure with zero persistent writes and no unsafe residue;
- Dialer success returning the actual exit IP;
- same configuration returning `NoChangesRequired` without backup, write, or restart;
- Runtime/profile/Script target-change races;
- marker deletion failure preserving its backup;
- NoChanges Runtime mismatch and selection mismatch;
- Recovery proxy-definition changes, selection restoration, and canonical JSON/YAML comparisons.

## P2 remediation results

### P2-1 Continue configuration refresh — Passed

Successful-result navigation re-runs environment detection and refreshes profile/Script hashes, current selector, and public IP. Selected non-sensitive targets remain; password is reloaded only from an enabled, unexpired encrypted cache. During refresh, the old environment is cleared and Next is disabled until the new snapshot is Ready.

### P2-2 Front-group scope message — Passed

If no safe current front Selector is identified, the UI states `当前环境仅支持直连模式。` and Direct remains available.

### P2-3 Same-exe deduplication — Passed

Targets are deduplicated by executable name, case-insensitively. Only one `PROCESS-NAME` rule is generated, and the UI states that the rule applies to all processes with the same executable name.

### P2-4 NoChangesRequired — Passed

Candidate Script bytes are compared with the current managed Script. A true no-change result performs read-only target and Runtime checks and creates no backup, marker, write, or Clash restart. A changed target or an unloaded/mismatched Runtime does not claim NoChanges.

### P2-5 Current-node display — Passed

The Step 1 current node comes from the safely identified actual front group, not the first arbitrary Selector. Provider-backed selections absent from the current inline node list show `暂不可测试`.

### P2-6 Backup/marker cleanup — Passed

Zero-write failures remove the marker before attempting backup cleanup. If marker deletion cannot be confirmed, the encrypted backup is preserved. Any persistent write enters formal Recovery. NoChanges creates neither resource.

## DPAPI temporary credential cache

- Payload: protocol, server, port, username, password, schema version, created UTC, expiry UTC.
- Protection: `ProtectedData` with `DataProtectionScope.CurrentUser`.
- Lifetime: one centralized 24-hour constant.
- Storage: `%LOCALAPPDATA%\AIWorkStation\credential-cache.bin`.
- Write: temporary file, exclusive create, WriteThrough, Flush, atomic Replace/Move, current-user ACL.
- Corrupt/expired data: ignored and deletion is attempted without blocking normal configuration.
- Clear button: immediate UI clear plus verified cache deletion result; failures are reported honestly.
- Authentication failure: cached password is removed while non-secret fields remain.
- Decrypted and serialized byte arrays are zeroed after use.
- Cache write failure is a non-blocking warning visible on the result page.

## Node latency UI

- Step 1 shows latency, status, and test time as a one-run observation.
- Only the actual selected front node is tested automatically.
- Test All uses Mihomo `/proxies/{url-encoded-name}/delay` with `expected=200-299`.
- Maximum concurrency is 4 and per-node timeout is 5 seconds.
- Cancellation stops remaining work; one node failure does not abort the list.
- Tests never change node selection, subscription data, Apply state, or eligibility.
- Leaving Step 1, rechecking, and closing detach/cancel the current latency run without stale UI writes.

## ViewModel and UI concurrency hardening

- Proxy input revision checks prevent an old asynchronous validation from marking edited input as verified.
- Apply disables and guards Confirm Step backward navigation; window close is also blocked until Apply/Recover finishes.
- Environment refresh clears the old snapshot and disables Next until the new snapshot is Ready.
- PasswordBox synchronization uses non-blocking Dispatcher dispatch during teardown.
- Opting out of temporary storage attempts deletion and reports a locked-file failure without claiming success.

## Final verification

| Check | Result |
|---|---|
| `dotnet build -c Release` | Passed — 0 warnings, 0 errors |
| `dotnet test -c Release --no-build` | Passed — 147/147, 0 failed, 0 skipped |
| win-x64 self-contained single-file publish, trimming disabled | Passed |
| Published executable UI Smoke | Passed — Step 1/2/3/4 loaded |
| Read-only Host diagnostics | Passed with host limitation |
| Secret Scan | Passed — no confirmed real secret |

The read-only Host report detected Clash, Mihomo, the current profile, 59 nodes, public-IP access, application search, route metadata, and a valid offline Runtime delta candidate. The current host also contains a pre-existing custom network configuration, so the product correctly classified it as Unsupported. In accordance with the acceptance boundary, no real Clash configuration was changed, no real proxy credential was used, no Clash process was restarted, and Apply was not invoked. The temporary Host/UI reports were deleted after extracting sanitized results.

## Code volume change

| Metric | Baseline | Final | Change |
|---|---:|---:|---:|
| Production source files | 40 | 43 | +3 |
| Test source files | 7 | 15 | +8 |
| Production LOC | 3308 | 5192 | +1884 |
| Test LOC | 982 | 3346 | +2364 |

Counts use production `.cs`/`.xaml`/`.resx` and test `.cs` physical lines, excluding generated `bin`/`obj` content.

## Remaining known issues and intentional scope limits

1. The current host has a pre-existing custom network configuration and is therefore Unsupported for automatic Apply. Real Apply readiness could not be reached without modifying user configuration, which was prohibited.
2. A Provider-backed current selection that is not materialized in the current inline subscription node list remains `暂不可测试` by design; no Provider management subsystem was added.
3. If a corrupt or expired DPAPI cache file is locked by another process, the app ignores it and deletion may be deferred. The remaining file is still DPAPI ciphertext and does not block configuration.

## Freeze statement

All remediation code and tests were complete before packaging. After the final build, test, publish, smoke, host diagnostics, and Secret Scan, feature development was frozen. Packaging adds no business-code, UI, test-logic, dependency, configuration-logic, or architecture changes.
