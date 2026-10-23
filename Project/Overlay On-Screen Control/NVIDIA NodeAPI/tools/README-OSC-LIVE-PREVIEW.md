# OSC Live Preview: Project Stable to Build Runtime

**Source of truth:** C:\My Project\NVIDIA-Shadowplay\Project\Overlay On-Screen Control\NVIDIA NodeAPI\osc

**Live runtime:** C:\My Project\NVIDIA-Shadowplay\build\NVIDIA ShadowPlay\Overlay On-Screen Control\NVIDIA NodeAPI\osc

NVIDIA NodeAPI (index.js) serves OSC static files relative to its own __dirname. Thus editing Project alone does NOT change a running Build CEF page. A headless browser mockup is NOT native CEF proof.

## Commands

From the Git repository root:

    $tool = ".\Project\Overlay On-Screen Control\NVIDIA NodeAPI\tools\osc-live-preview.cjs"
    node $tool --plan
    node $tool --apply
    node $tool --status
    node $tool --rollback SESSION_ID

* PLAN is read-only. It prints all differing files and old/new SHA-256.
* APPLY requires an explicit call. It backs up ALL changed Runtime files before the first write, verifies SHA-256, and writes only changed, Git-tracked static OSC frontend files.
* STATUS shows earlier sessions.
* ROLLBACK restores exactly one session; it will REFUSE to overwrite a file changed by another process after that preview.
* No background watching or automatic sync is configured.

## Safety and boundaries

The sync runs only on Git Stable with clean OSC source. It excludes runtime-generated mods/launcher-language.json, config.js, user-config.js, backups and nonstatic files. It never deletes unknown Build files, and never mirrors or restarts Capture, Container, Launcher, Hooks, NodeAPI backend or other subsystems. The main Build remains the working Runtime rather than a developer source tree.

Every preview has a backup manifest here:

    build_test/OSC-Live-Preview/sessions/SESSION_ID/manifest.json
    build_test/OSC-Live-Preview/sessions/SESSION_ID/files/

Never delete these backups while a preview is active. Keep the session ID from APPLY if you need to ROLLBACK. This is a reversible frontend preview, not a production release or whole-solution Build.

## Real CEF verification

Once NVIDIA OSC and its NodeAPI are running, reopen or refresh the overlay and inspect:

    powershell -NoProfile -ExecutionPolicy Bypass -File ".\Project\Overlay On-Screen Control\NVIDIA NodeAPI\tools\osc-preview-audit.ps1"

The report compares live HTTP 59011 content with Source and Runtime SHA-256. If liveOrigin is BUILD and projectEqualsBuild is True, live CEF receives the source-mirrored content. If the HTTP port is OFFLINE, real CEF has NOT been tested; the sync intentionally does not start or restart native services.

## Tests

    node --test ".\Project\Overlay On-Screen Control\NVIDIA NodeAPI\tests\oscLivePreview.test.cjs"

Run relevant native UI, Main-menu, OSC Motion, Game Filter and Language Sync tests before APPLY.

**OSC constraints:** use NVIDIA md-menu/md-icon/ICO and OSC Host scale; keep only one layout owner; synchronize Motion prepositioning before animation; preserve Launcher locale and runtime state. Source on Stable remains the authority even when Runtime is temporarily synced for Preview.
