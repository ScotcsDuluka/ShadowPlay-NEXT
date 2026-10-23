# Ansel Adapter — live Game Filter

This adapter owns filter control. The OSC Adapter Hook owns overlay composition
and input. They have separate ReShade registrations and SDKs.

```text
OSC Angular controls / three per-game slots
  ↕ NvCamera REST + /NvCamera/v.1.0/Notifications
NodeAPI shims/game-filter-route.js → shims/ansel-client.js
  ↕ bounded JSON lines, local named pipe NVIDIAAnsel.<pid>.<runtime-serial>
NVIDIA Ansel Adapter.dll
  ↕ commands, catalog and verified readback on the ReShade present thread
D3D11 ReShade effect runtime
```

## Build and deployment

From the repository root, run `Scripts/build-ansel.ps1`. It builds the native
adapter, OSC hook, attachment host and native tests, then runs native and Node
HTTP/pipe integration tests. No CEF rebuild is needed.

`Scripts/build-ansel.ps1 -Stage` backs up and stages the adapter DLLs and Node/UI
files. Close games first: loaded native DLLs cannot be replaced. The script
resolves the active adapter directory from the runtime's `nvcontainer.json`,
including a configured `CrossIntegrity` directory. It preserves that directory's
existing host executable. Restart the product's Web Helper to load the new
backend. Running OSC hosts hot-load the allowed `ansel-game-filter.js` mod; fresh
pages also load it from their HTML entry. A Desktop host restart is unnecessary.
The script does not restart Desktop OSC itself.

The normal `Scripts/build-dev.ps1` also builds and stages Ansel Adapter. When the
OSC hook successfully registers with ReShade it loads the sibling Ansel DLL.
The existing Container/game-watcher configuration still decides whether to
attach the OSC hook; this implementation does not enable injection globally.

For an already-hooked game, the rebuilt attachment host accepts
`--ansel-only true` in addition to its existing `--game-pid`, `--game-name` and
`--game-executable` arguments. It loads only Ansel, avoiding a second compositor.
The Ansel DLL uses ReShade's supported DllMain registration entry point and can
also be installed as an `.addon64` by a deployment owner. Injected adapters are
pinned for process lifetime; runtime pipes and handles close on runtime destroy.
Hot DLL unload/replacement is intentionally not supported.

## Runtime contract

- D3D11 only. Requires a ReShade build supporting add-ons and SDK API v8.
- Each effect runtime gets its own pipe and opaque session. Reload increments
  the generation. Mutations must carry the exact session and generation read by
  the client; old requests receive `409 STALE_SESSION`.
- No ReShade handle crosses the process boundary. Filter IDs encode effect file
  and technique name; control IDs are uniform names resolved from the current
  catalog. Reload clears all cached native handles and baseline values.
- Pipe work is overlapped, bounded to 1 MiB and serviced from `reshade_present`.
  There are no pipe worker threads calling ReShade, blocking pipe reads or
  background runtime pointers. Unresponsive/minimized render loops time out.
- Commands carry a deadline, so a queued expired slider command cannot apply
  later after its caller has timed out. The client does not retry mutations.
- Discovery selects the foreground runtime, or the sole responding runtime.
  Multiple ambiguous runtimes fail with `409`; callers may select a game with
  `X-Ansel-Game-Pid`. Writes remain pinned to the discovered runtime.
- An editing session pins the game/runtime across foreground changes. Reload
  invalidates that session until the UI renegotiates it. Slider drags have one
  in-flight write per control and retain the newest pending value, avoiding a
  backlog of obsolete positions.
- Success includes fresh state. Uniform and enabled-state writes are read back;
  a rejected runtime write is reported as an error rather than empty success.

Supported operations: live catalog and process information; filter insertion,
removal, stack clearing and batch reordering/preset application; scalar/vector
numeric and boolean uniforms (up to four components); global OSC-stack enable;
reset to values observed when the runtime/effects were attached or reloaded.
Engine-driven (`source`) and hidden uniforms, matrices and uniform arrays are
excluded. Unbounded numeric uniforms use edits instead of fabricated sliders.
ReShade `ui_type=combo` and `ui_type=radio` annotations with a valid NUL-separated `ui_items` list and a zero-based integer uniform now appear as NVIDIA OSC list controls, with actual option labels. Unrecognized or out-of-range values retain a numeric editor rather than silently selecting another shader mode. The adapter also passes `ui_tooltip`, `ui_category` and `ui_type` metadata to the OSC catalog (display of those metadata fields is still limited by the NVIDIA control templates). This is ReShade control compatibility, not a complete replacement for ReShade's overlay: color pickers, shaders' static text, full matrix/array controls, and ReShade settings/add-on/statistics tabs are not imported.

Preset batches validate the complete input before the first write. Hardware or
other add-on rejection during application is reported; this is not a GPU
transaction/rollback guarantee. Existing out-of-editor-range preset values can
be reapplied unchanged; new out-of-range values are rejected. Float32 boundary
rounding is normalized.

With a live ReShade add-on, OSC Game Filter mirrors ReShade rather than treating
its browser-local `ModsSlotStorage` data as the source of truth. OSC's buttons
1/2/3 select real INI files at the **Game Root** (installation directory), not
inside a nested `Binaries/Win64` directory:

- `<Game Root>/NVIDIA-GameFilter-Preset1.ini`
- `<Game Root>/NVIDIA-GameFilter-Preset2.ini`
- `<Game Root>/NVIDIA-GameFilter-Preset3.ini`

The native path resolver recognizes Unreal Engine's `Game/Binaries/Win64`
layout and uses root-level game launchers, `ReShade.ini`, `Engine` and
install markers to find the correct Game Root. For conventional games whose
executable already sits at Game Root, it uses that directory. The resolved
path is cached, and exposed in `GameFilter/State.preset.directory`.
Relative ReShade preset paths are resolved against Game Root first, then the
nested executable directory for legacy INIs; the mutable process working
directory is never used. A slot INI already present at Game Root can be
activated even if ReShade has not yet selected a current preset.

Selecting a missing INI initializes it by asking ReShade to save the currently
active preset and copying that file. If an older version of OSC stored
the same slot INI alongside a nested game executable, that INI is copied to
Game Root instead; the original is kept. Existing Game Root target INIs are
never overwritten by initialization or migration. ReShade's
`set_current_preset_path` saves the outgoing
preset and activates the target; `save_current_preset` persists OSC edits after
750 ms without additional edits. OSC's Off toggles the ReShade global effects
flag without deleting the saved technique stack. This also applies to the
legacy NVIDIA `EnableMods` API: in preset mode it must toggle global effects,
not disable every technique individually. Native snapshots read the real
global ReShade effect flag, including when it is changed outside OSC.
The Node bridge never writes Shader INI syntax itself. Saving requires write
access to the game's folder;
when this is denied the native command fails explicitly (403) and keeps the
existing preset. No INI file is created until OSC activates a slot (including
its initial default slot when the editor first opens).
Old OSC localStorage entries remain a UI cache and are not automatically
converted into INIs. A new game's first slot starts as a copy of the currently
active ReShade preset. Reset still means the session's loaded-preset baseline.

The earlier standalone OSC preset implementation is still used for test runtimes
without native ReShade preset support.

The live middleware owns NvCamera before Container's UI simulations, including
when Container test mode is enabled. Unsupported photography/camera commands
return `501`; `Capture/Control` here acknowledges the filter-editing session and
does not pause games or take screenshots. `EnableMods` accepts the OSC
`globalEnable` field. When no runtime exists it records a pending preference and
returns `applied:false`, not a claim that an effect was changed.

Notifications include the original `available`, `captureControlChange`,
`filters`, `currentFilter`, `setFilterAttribute`, `filterAndAttributes`,
`multipleSetFilterAndAttributes`, and `nvCameraReady` shapes. The additional
`anselState` event and `GET /Duluka/v.1.0/GameFilter/State` expose connection,
session, generation, actual controls and stack. The legacy UI entry shim checks
this live state before using its local/offline capability path, rather than
assuming NVIDIA cloud allowlists or Container test replies establish support.
An open OSC Game Filter editor refreshes the native catalog on its approximately
two-second heartbeat (rather than replaying a stale Node cache). Removed native
techniques emit an explicit empty stack-slot notification, and readback values
replace browser savedConfigs before the stock NVIDIA notification handler runs.
The closed editor/main menu retains inexpensive cached availability checks.
The filter accordion patch retries until its real NVIDIA controller exists,
avoiding a lost first click when route creation outruns Angular linking.

## Validation

- `tests/AnselSessionTests.cpp`: range/dimension validation, deadlines, batches,
  stack order, toggles, baseline reset and stale generations.
- `NVIDIA NodeAPI/tests/gameFilter.test.js`: real Windows named-pipe discovery
  and transport with an explicitly fake native effect runtime, HTTP shapes,
  notifications, readback, preset completion, expiry and disconnect/error paths.
- `NVIDIA NodeAPI/tests/gameFilterUi.test.js`: live entry checks, initial stack
  import, existing/other-game slot preservation, hot-load idempotency and slider
  coalescing against the UI module's actual public service wrappers.
- `Scripts/verify-ansel-live.js <pid>`: real D3D11 ReShade FXAA/Subpix slider and
  toggle validation through the HTTP middleware. Requires active FXAA as the last
  technique; restores the changed parameter and original stack in `finally`.
  Writes `Build/AnselValidation/live-result.json` only after restoration succeeds.
  Numeric readback proves runtime application; it is not a pixel-comparison test.

## Bundled dependencies

Live validation on 2026-10-20 used SMASH LEGENDS, D3D11, and ReShade 6.8.0.
Both hook and Ansel add-on loaded automatically on game startup. The running OSC
opened `#/base/mods`, rendered the imported three-technique stack in slot 1,
and exposed 33/10/3 controls for RadiantGI/BaBa PHDR/FXAA. Invoking the actual
FXAA slider callback changed native Subpix from 0.25 to 0.75; restoring 0.25 and
closing the menu preserved the enabled stack. Evidence is recorded in
`Build/AnselValidation/ui-live-result.json` and `live-result.json`.
This checks the live controller and native readback, not physical mouse dragging
or visual image quality. RadiantGI separately reports a shader warning in ReShade.


- ReShade SDK v5.9.2 / API v8, unmodified headers from
  <https://github.com/crosire/reshade/tree/v5.9.2/include>.
  License: `include/reshade-api-v8/LICENSE.md`.
- nlohmann/json v3.11.3 single header from
  <https://github.com/nlohmann/json/tree/v3.11.3>.
  License: `include/nlohmann/LICENSE.MIT`.

The compositor keeps its existing v3 SDK. Different SDK versions are isolated
in separate DLLs, not mixed effect-runtime vtables in one translation unit.
