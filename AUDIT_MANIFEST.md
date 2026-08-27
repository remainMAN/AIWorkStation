# Audit Manifest

Project:
AIWorkStationClean

Audit Stage:
V1 Backend Finalization / Backend Frozen

Source Path:
D:\AIWorkStationClean

Audit Package Created At:
2026-08-25 10:24:29 -07:00

Source:
Frozen

Formal Pipeline:
Check -> Build -> Validate -> Backup -> Write -> Reload -> Verify -> Recover

Baseline Build:
Warnings: 0
Errors: 0

Baseline Tests:
Passed: 180
Failed: 0
Skipped: 0
Total: 180

Final Build:
Warnings: 0
Errors: 0

Final Tests:
Passed: 220
Failed: 0
Skipped: 0
Total: 220

Publish:
Passed - win-x64, SelfContained, SingleFile, PublishTrimmed=false

UI Smoke:
Passed - published executable created a responsive AI WorkStation main window

Read-only Host Acceptance:
Running ChatGPT: Found
Running Codex: Found
OpenAI Preset ChatGPT: Found
OpenAI Preset Codex: Found
Clash Mode: rule
Dialer Group: FlyintPro
Current Front Node: Hongkong 016
Direct Available: Yes
Dialer Available: Yes
Real Apply Performed: No

Secret Scan:
Passed

Production Source Files:
44

Test Source Files:
17

Production LOC:
5417

Test LOC:
3778

Excluded Directories:
bin
obj
.vs
.git
TestResults
coverage
packages
artifacts
dist
.tools
.tools-wix5

Excluded Runtime Data:
credential-cache.bin
candidate-*.yaml
transaction.json
backup workspaces
real Clash configuration
real subscription data
real proxy credentials

Current Audit Documents:
V1_BACKEND_FINALIZATION_REPORT.md
NO_NEW_GATE_FINAL_AUDIT.md
CHANGED_FILES.txt
SOURCE_TREE.txt
AUDIT_MANIFEST.md

New Blocking Condition Introduced:
No

Second Pipeline:
No

Production Source Modified During Packaging:
No
