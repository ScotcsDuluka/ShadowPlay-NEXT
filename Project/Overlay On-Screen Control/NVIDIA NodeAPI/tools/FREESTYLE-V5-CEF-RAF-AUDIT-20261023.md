# ShadowPlay NEXT Freestyle V5 — Live CEF RAF/Motion audit (2026-10-23)

**Status: Partial CEF verification; deployment of commit 764809dca8 is blocked by native activation, not approved.**

## Scope and protection

- Git branch Stable, previously verified HEAD 764809dca8.
- Source-only optimization in osc/mods/nv-next-v5-native.js suppresses RAF scheduling while native OSC Motion is blocked and retries when open/idle. Selected offline suite: 135/135 passed.
- Source and Build native adapter differ intentionally. **Do not claim they were synced.**
- All live audit scripts stored only under build_test/OSC-Live-Preview/v5-session-20261023/.
- Fresh full CEF/localStorage backup before testing: pre-raf-cef-audit-20261023-2033.json.
- Actual CEF Chromium target: http://localhost:59011/index.html#/base; viewport 1920x1200, DPR 0.875, V5 enabled, Edit false.
- Preview used CEF DevTools Network interception for **only** mods/nv-next-v5-native.js. No Runtime Build binaries or frontend files were overwritten, no user Layout changes and no Capture/Hook edits.

## Evidence

1. **Closed/idle source interception PASS.**
   Source preview served Project/native adapter with SHA256
   E0B32ADA4CA8A70E01211D992191DCA1A65E45A5A42C09A1A1F42407C6FC8A97.
   Actual CEF linked 12 real NVIDIA md-menu elements and 12 V5-styled native tiles.
   While route was #/base/main-menu but Host was confirmed idle/closed,
   observed native adapter RAF calls over approximately 1.9 seconds: **0**.
   Existing Build adapter under the same conditions also recorded **0**.
   Thus **no measured CPU-performance improvement** is claimed from this CEF case.
   The source patch is not regressing the idle/closed behavior.
   Details: raf-motion-source-preview-result.json.

2. **Open/transition source preview INCONCLUSIVE / failed gate.**
   Two independent audits attempted opening Native Host with original CEF Motion
   while serving the source native adapter (without modifying Build). Each linked
   12 native controls and computed six Groups/12 cells, but Motion returned to
   idle/closed (requestedTarget=closed) and native menu x approx. 1922px,
   which is normal for off-screen/closed Motion, not evidence of a bad layout.
   Host log includes `OSC activation failed after retries` and
   route #/base triggers the intended Host closure.
   Reordering Host show/base/reload/main did not yield a stable open during
   this audit. Native activation behavior is outside this V5-only scope.
   Details: v5-source-motion-open-audit-20261023.json.

3. **Restoration PASS.**
   Both scripts disabled source interception and reloaded the original Build
   frontend. CEF route restored to #/base, idle/closed; original V5 enabled
   flag and serialized user Layout were byte-for-byte preserved. Do not restore
   an older backup over newer user changes.

## Next action / release gate

- When Native Host activation is reliably available again, re-run the source
  preview using original Host sequencing and verify the genuine idle/open
  CEF geometry, 12 controls, transitions, and absence of RAF churn.
- Do **not** deploy commit 764809dca8 into Build on the basis of offline/closed
  results alone. If validated, use targeted backed-up sync through
  tools/osc-live-preview.cjs with --files mods/nv-next-v5-native.js and verify SHA.
- Check Native Host activation failure with the Host owner. The OSC Host and
  renderer files are already dirty under another workstream — do not edit,
  stage or overwrite them here. No full Build replacement, git reset or
  layout reset.
- Earlier combined CEF integration passed 18/18 before this source optimization.
  That previous pass must not be represented as validation of the new source
  RAF optimization.

**Overall decision: hold source-only patch on Stable; Build remains untouched.**
