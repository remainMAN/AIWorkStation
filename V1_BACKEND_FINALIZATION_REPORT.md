# AIWorkStationClean V1 Backend Finalization Report

Created: 2026-08-25 10:24:29 -07:00

## Status

- Source: Frozen
- Backend: Frozen
- Formal pipeline: Check -> Build -> Validate -> Backup -> Write -> Reload -> Verify -> Recover
- New blocking condition introduced: No
- Second pipeline introduced: No

## Baseline

- Build warnings: 0
- Build errors: 0
- Tests passed: 180
- Tests failed: 0
- Tests skipped: 0
- Production source files: 44
- Production LOC: 5098
- Test source files: 16
- Test LOC: 3343

## Bug 1 - Application Discovery

- Running `codex.exe` remains discoverable when `MainModule.FileName` cannot be read; the fallback uses `ProcessName` and leaves the path unknown.
- Running `ChatGPT.exe` uses the same fallback.
- Existing registered-package discovery remains a fail-soft application source and does not scan WindowsApps recursively.
- OpenAI preset matching remains based on discovered executable names, not hard-coded script rules.
- PROCESS-NAME targets are deduplicated with `OrdinalIgnoreCase`.
- Read-only host verification found both `ChatGPT.exe` and `codex.exe`; the OpenAI preset returned both exact executable names.
- No matching ChatGPT/Codex AppX registration exists on this host, so closed-package host acceptance is not applicable; simulated package tests pass.

## Bug 2 - NoTraffic and Recovery

- No target traffic and no mismatch returns a successful `NotObserved` result.
- NoTraffic does not start Recovery.
- A new connection observed on the wrong route still fails Verify and enters the existing Recover path.
- Partial traffic remains successful and reports unobserved targets separately.
- Recovery no longer treats the whole `/configs` SHA-256 as a hard equality requirement.
- Stable managed group, selection, members, rules and managed proxy definitions remain recovery semantics.

## Bug 3 - Dialer Consistency

- Existing selector selection order remains: profile-named selector, MATCH target, most-referenced safe selector, then exposed safe choices.
- Read-only host result: group `FlyintPro`, current node `Hongkong 016`, current AI static selection `AI静态出口-链式`.
- The UI derives its transport display from the current runtime selection and displays the actual front group/node.
- Dialer mode places the dialer exit first; Direct mode places the direct exit first.
- Reload is followed by explicit controller selection of the final transport.

## Audit A - Legacy Script Migration

- Canonical managed script version is VERSION 2.
- Exact known single-exit V1 and dual-exit V1 templates are recognized and regenerated through the existing Build flow.
- Modified or appended V1-like user scripts remain protected from overwrite.

## Audit B - Temporary Runtime Restore

- Managed proxy definitions compare stable hashes derived from name, type, server, port, dialer-proxy and credential fields.
- Credential values are never emitted; only irreversible SHA-256 hashes participate in semantic comparison.
- Server, port and dialer residue mismatches fail existing temporary-runtime recovery verification.

## Audit C - Post-write Verification

- Post-reload verification compares proxy definitions, group order, group selection and PROCESS-NAME rules against the final route.
- Actual exit probing runs inside the existing Verify stage through a temporary domain-rule probe derived from the formal runtime and explicitly selected AI static group.
- The probe is removed immediately, the formal runtime is restored, and its stable semantics are rechecked before RouteVerifier runs.
- Post-write exit must match the pre-write actual exit and any configured expected exit; mismatches use the existing Verify -> Recover path.

## Audit D - Malformed Configuration

- YAML, JSON, duplicate UID, I/O, permission and supported parse failures return Chinese diagnostic text without an unhandled window crash.
- Global mode adds a warning only; it does not change Supported status or disable Apply.

## Audit E - Connection Baseline

- RouteVerifier captures existing non-empty connection IDs before polling.
- Pre-existing connections do not establish WrongRoute for the current Apply.
- Each poll recomputes state from the current snapshot; early mismatches are not accumulated forever.
- New correct, new wrong and no-traffic semantics are Verified, WrongRoute and NotObserved respectively.

## Read-only Host Verification

- Running Codex found: Passed
- Running ChatGPT found: Passed
- OpenAI preset includes ChatGPT: Passed
- OpenAI preset includes Codex: Passed
- Clash mode: rule
- Dialer group: FlyintPro
- Current front node: Hongkong 016
- Direct available: Yes
- Dialer available: Yes
- Real Apply/Reload/Script write performed: No

## Final Verification

- Build: Passed, 0 warnings, 0 errors
- Tests: Passed, 220 passed, 0 failed, 0 skipped
- Publish: Passed, win-x64, SelfContained, SingleFile, PublishTrimmed=false
- UI smoke: Passed; published executable created a responsive `AI WorkStation` main window and the smoke process was then closed.
- Production source files: 44
- Production LOC: 5417
- Production LOC net change: +319
- Test source files: 17
- Test LOC: 3778
- Test LOC net change: +435
- Secret scan: Passed

## Remaining Known Issues

- This host has no matching registered ChatGPT/Codex AppX package, so installed-but-closed package behavior is verified by automated tests rather than host evidence.
- No real Apply, Clash reload or credential-backed exit request was executed on the host because the task explicitly restricted host verification to read-only operations.
- UI visual redesign is intentionally outside this backend finalization and remains the next project phase.
