# NVIDIA OSC Group Layout A — V5 sandbox

Status: opt-in prototype. Do NOT auto-enable on Stable or deploy over the live Build before real CEF visual tests.

Model: Panel -> Group -> Block -> Slot -> Control.
- Panels: left / center / right, each accepts any number of Groups.
- Group: fixed 240x240 NVIDIA-native coordinate space.
- Block: Small or Side = 80px high; Big = 240px high.
- Three Small/Side rows can fit a Group; Big fills an entire Group.
- Slots hold one native Control each; empty Slots are allowed.
- Settings is mandatory, movable, and cannot be removed.

Presets (8):
- Small: triple (80/80/80), wideLeft (160/80), wideRight (80/160), single (240).
- Side: single (240 with icon/text/subtext), detailLeft (160/80), detailRight (80/160).
- Big: single (240x240).

New active implementation: mods/main-menu-group-model.js and main-menu-group-editor.js,
with main-menu-group-editor.css. The older tracked mods/main-menu-group-layout-a.js
is a separate legacy prototype used by its own regression tests; NEVER load both
models into the same runtime since both use window.NvGroupLayoutA.

Runtime: index.html loads the V5 model and editor only AFTER the V4 scripts.
V5 is OFF by default and adds a Group Layout A opt-in action to the V4 Customize
toolbar. On disabling, the original V4 editor appears again.

Storage:
- V4: DulukaMainMenuUnifiedV4
- V5: DulukaMainMenuGroupLayoutA_V5
- Opt-in: DulukaMainMenuGroupLayoutAEnabled

V5 migration reads V4 without writing over its layout or V2 storage. Native
rendering delegates to V5 model only while V5 is explicitly enabled.

Regression: mainMenuGroupEditor.test.js tests 8 presets, migrations, no duplicate
Controls, Settings protection, swaps, capacity, Group moves, scaling, script
load order, opt-in toggle, and storage isolation. Run alongside existing
mainMenuGroupLayoutA, mainMenuUnifiedNative, mainMenuUnifiedModel,
mainMenuLiveEditor, mainMenuCustomize, and oscMotion tests. Motion suite
requires build_test/NVIDIA Background_Test/selected-motion-timing.json.

Next task: native OSC CEF smoke/visual review before enabling this on a real
Build. Do not count automated tests as visual approval.
