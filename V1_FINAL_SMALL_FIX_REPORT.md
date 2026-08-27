# AIWorkStationClean V1 Final Small Fix Report

Created: 2026-08-25 19:32:41 -07:00

## Final State

- Source: Frozen
- Backend: Frozen
- Next stage: UI Design
- Formal pipeline remains: Check -> Build -> Validate -> Backup -> Write -> Reload -> Verify -> Recover

## Baseline

- Build warnings: 0
- Build errors: 0
- Tests: 220 passed, 0 failed, 0 skipped
- Production source: 44 files, 5417 LOC
- Test source: 17 files, 3778 LOC

## Small Fix A - Application Discovery

- Added a minimal `浏览 EXE` button to the existing Step 2 search row.
- Uses the standard Windows OpenFileDialog with an `*.exe` filter, single selection and file-exists behavior.
- A manual selection creates the existing `ApplicationTarget` model with display name, executable name, full path and source `Manual`.
- The selected target continues through the existing PROCESS-NAME route path; no PROCESS-PATH, package route or new rule type was added.
- Manual and automatically discovered targets continue to deduplicate by executable name with `OrdinalIgnoreCase` semantics.
- Cancellation leaves the current selection and status unchanged.
- Missing/invalid and unreadable files show the requested local Chinese messages and do not affect environment support.
- Running process, AppX/MSIX, App Paths, Start Menu and Uninstall Registry sources remain enabled.
- Registry, shortcut and package candidates now use a shared per-item exception boundary. A damaged item is skipped while later valid items from the same source remain available.
- No full-disk EXE scan was added. Existing Start Menu shortcut enumeration and top-level Uninstall install-directory probing remain scoped as before.

## Small Fix B - Audit B Correction

- `CaptureRuntimeSemanticBaselineAsync` no longer reads or hashes `/proxies` server, port, username, password or dialer definition fields.
- A missing controller field is no longer converted into an empty-string definition hash or treated as proof that a definition was restored.
- Temporary runtime recovery continues to verify controller health, managed object names, managed group existence, group members, group selection and managed rules.
- When no AIWS objects existed before validation, restored absence is compared and any candidate object/rule residue fails semantic equality.
- Current profile and Extension hash checks remain unchanged in the existing temporary restore flow.
- Persistent definition facts remain sourced from Runtime YAML/Extension data. Formal Recovery and post-write verification still overlay YAML-derived managed definition hashes.
- Fake runtime `/proxies` responses now expose only type, managed object names and Selector state; unsupported server, port and credential fields were removed.
- Pre-existing AIWS actual-exit restore comparison is Not Applicable in this round because no reliable passive group-specific exit observation exists before changing Runtime. Per the approved fallback, inability to obtain it does not block Apply; stable runtime semantics remain the recovery evidence.

## Audit C

- Audit C production code was not modified in this round.
- Post-write Runtime YAML definition verification remains enabled.
- Post-write actual exit verification and pre/post exit comparison remain enabled.
- Verify failures continue through the existing Recover path.

## Tests Added or Corrected

- ManualExeSelection_CreatesApplicationTarget
- ManualExeSelection_UsesExecutableName
- ManualExeSelection_DeduplicatesSameExecutableName
- ManualExeSelection_CancelDoesNothing
- ManualExeSelection_InvalidFileShowsError
- ManualExeSelection_UnreadableFileShowsError
- OneBrokenUninstallEntry_DoesNotDiscardOtherEntries
- OneBrokenShortcut_DoesNotDiscardOtherShortcuts
- OneBrokenPackageItem_DoesNotDiscardOtherPackages
- ApplicationSourceFailure_DoesNotCrashFinder
- RuntimeProxiesWithoutServerPort_DoesNotProduceFakeDefinitionProof
- RuntimeProxiesWithoutCredentials_DoesNotProduceFakeCredentialProof
- TemporaryRestore_OriginalObjectsAbsent_RemovesCandidateObjects
- TemporaryRestore_GroupMembersRestored_Passes
- TemporaryRestore_GroupSelectionRestored_Passes
- TemporaryRestore_RulesRestored_Passes
- FakeRuntime_DoesNotExposeUnsupportedServerPortFields
- TemporaryRuntimeWithoutDefinitionFields_UsesStableSemantics

## Changed Files

Production:

- AIWorkStation/Services/ApplicationFinder.cs
- AIWorkStation/Services/PackagedApplicationSource.cs
- AIWorkStation/Services/RecoveryService.cs
- AIWorkStation/ViewModels/MainViewModel.cs
- AIWorkStation/Views/RoutingStep.xaml

Tests:

- AIWorkStation.Tests/FinalSmallFixApplicationTests.cs (new)
- AIWorkStation.Tests/RecoverySemanticTests.cs
- AIWorkStation.Tests/ApplyEngineIntegrationTests.cs

Report:

- V1_FINAL_SMALL_FIX_REPORT.md (new)

## No New Gate Audit

- New blocking condition introduced: No
- Existing blocking condition strengthened: No
- New Gate: No
- New Readiness: No
- New Authorization: No
- New Unsupported condition: No
- Second pipeline: No
- Full-disk application scan: No

The only gate-keyword match in changed production files is the pre-existing `MainViewModel.CanApply` busy/recovery-failed command guard. Its condition was not changed. Manual file validation only rejects the current selected file and lets the user choose another application.

## Final Verification

- Build: Passed, 0 warnings, 0 errors
- Tests: Passed, 233 passed, 0 failed, 0 skipped
- Publish: Passed, win-x64, SelfContained, SingleFile, PublishTrimmed=false
- UI smoke: Passed; the published executable created a responsive `AI WorkStation` main window and the smoke process was closed afterward.
- Manual browse behavior: Passed through command-level tests using harmless temporary EXE files; no real Apply was executed.
- Production source: 44 files, 5514 LOC, net +97
- Test source: 18 files, 3923 LOC, net +145
- Secret scan: Passed; no non-test high-confidence secret and no `.env` file found.

## Remaining Known Issues

- Pre-existing AIWS actual-exit restore comparison remains Not Applicable unless a reliable non-invasive group-specific observation becomes available. This does not add a gate and does not weaken the required group/member/selection/rule/object recovery checks.
- UI visual redesign is intentionally not part of this small fix and is the next stage.

## Freeze Declaration

Backend Frozen. Stop backend development. Further work proceeds only in the UI Design stage unless the user explicitly authorizes a new backend fix for a real blocking defect.
