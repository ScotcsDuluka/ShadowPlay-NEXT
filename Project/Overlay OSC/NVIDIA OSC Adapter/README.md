# NVIDIA OSC Adapter - DXGI proof of concept

The OSC Adapter is separate from the Filter Adapter. Its host is launched by
NvContainer for an ApplicationOntology-matched game; the host loads
`NVIDIA OSC Adapter Hook.dll` into that x64 game process. The Filter Adapter/ReShade add-on is
not used here.

## What "embed OSC" means in this design

The adapter transports and composites the complete CEF `PET_VIEW` surface from
`NVIDIA OSC.exe`, not individual toast cards. OSC.exe remains the owner of the
CEF runtime, UI, and application logic; the in-game DLL is only the graphics
backend that draws that surface during the game's `Present`. This is full
surface composition, not injection of the OSC process or its UI logic into the
game. Pointer and keyboard forwarding are wired through the OSC publisher, but
end-to-end interaction with the full in-game surface is not yet live-game
verified.

### Installed NVIDIA path observed in Smash Legends

Read-only inspection of the current game's `CaptureCore.log` and loaded modules
shows a useful distinction:

- The game loads NVIDIA's signed `nvspcap64.dll`, but does not load
  `NVIDIA Overlay.exe` or `libcef.dll`; the OSC browser/runtime remains in a
  separate process.
- NVIDIA creates a `CShadowPlayProxyShim` session with the game's D3D device
  and renderer, then its in-game `CSPOscOverlayRenderer` processes a shared GPU
  resource (`hResource`) for the OSC surface. The log also shows OSC input
  redirection being enabled in the game.
- This is evidence for a native game-side renderer/proxy plus a separately
  hosted OSC UI, not proof that the entire OSC runtime is injected. NVIDIA's
  observed DDI/proxy path also differs from this project's current DXGI
  `Present`-vtable proof of concept.
- In this capture, NVIDIA logged repeated
  `SetGpuTexture failed = 0xC000000D` (`STATUS_INVALID_PARAMETER`). Therefore
  module presence and a created proxy session do not alone prove successful
  texture composition; the same per-stage success evidence is needed for this
  adapter.

## Current milestone

- The x64 native DLL installs a process-local hook at
  `IDXGISwapChain::Present` and opens the shared CEF D3D11 texture. When ReShade
  is present, the DLL registers as a ReShade add-on and composites from the
  `reshade_present` callback, which is documented to run after ReShade renders
  its overlay and therefore after its post-processing effects. This keeps OSC
  out of ReShade shader input. Games without ReShade keep the DXGI `Present`
  compositor fallback.
- `NVIDIA OSC.exe` copies each CEF surface into a dedicated D3D11 NT shared
  texture, synchronizes access with a keyed mutex, and publishes that handle,
  dimensions, and source PID through `Local\NVIDIAOSC.Frame.v2`. This avoids
  passing CEF's runtime-owned handle into the game. A single OSC process owns
  the publisher slot. The game hook reports its DXGI adapter LUID through a
  separate local mapping. When OSC and the game use different adapters, OSC
  reads the CEF frame back through a staging texture and uploads it to a shared
  texture created on the game's adapter. The staging ring copies and uploads
  only CEF dirty rectangles after its initial full-frame transfer, and a short
  UI-thread timer drains completed readbacks even when the CEF surface becomes
  idle; the usual same-adapter path stays a GPU copy. The adapter duplicates NT
  handles into the game process and retains a legacy DXGI shared-handle
  fallback for compatibility.
- The compositor swaps a D3D11.1 context state around its draw call so the
  game's pipeline state is restored. In ReShade mode, it draws to the current
  runtime back buffer from the post-effects callback; in fallback mode, it
  draws before the original `Present`.
- Rendering is gated by the mapped game PID being the foreground process, or
  by `NVIDIA Overlay.exe` while that game is still running. A 100 ms window
  timer detects focus changes even while the CEF surface is idle.
  `NVIDIA OSC.exe` hides both desktop overlay windows and publishes frames only
  in game-focus mode; when focus leaves, frame publication stops and desktop
  OSC returns without activation. The game hook retains a local GPU copy of
  the latest published frame and refreshes it only when the frame number
  changes, so a busy keyed mutex drops an update instead of making the overlay
  blink off. Mouse clicks inside registered toast regions,
  or across the screen while the modal OSC UI is open, are forwarded to CEF.
  Keyboard capture is enabled only after clicking the open modal UI and is
  released when it closes or game focus changes, so routine gameplay input is
  not intercepted. System shortcuts such as Win, Alt+Tab, and Alt+F4 remain
  with Windows/game.
- Cross-adapter readback uses a three-slot staging/query ring. It polls completed
  GPU copies and attempts keyed-mutex acquisition without waiting, dropping
  frames when all slots are occupied instead of blocking the CEF paint callback.
  A busy consumer leaves the completed slot queued for retry; if an update is
  dropped, the next submitted slot refreshes the full frame to recover any
  missed dirty regions. This removes the previous full-frame CPU upload on
  every update; actual game FPS and input behavior still require live-game
  verification.
- Each opened source texture retains a process handle. The compositor validates
  the bridge owner PID and stops using/releasing the cached texture when the
  OSC publisher process exits, rather than continuing to draw a stale frame.
- The x64 host verifies the target PID still belongs to the expected executable
  and checks its architecture before loading the DLL.
- NvContainer keeps the feature disabled by default. Enable `oscAdapter` only
  for a controlled, non-protected test process.
- The D3D11 smoke test verifies actual back-buffer pixels after composition.
- The native hook writes periodic Present, bridge, texture-open, draw, swap-chain
  size, and HRESULT diagnostics to
  `%LOCALAPPDATA%\NVIDIA\OSC Adapter\Logs\adapter-hook-<pid>.log`.
  The first status is written on the first intercepted Present, then every
  1,200 presents. The file may also contain hook initialization messages, so
  check specifically for a `Present diagnostics` line to confirm the callback;
  the adapter-host log separately confirms whether DLL loading succeeded.

The current render path is D3D11/DXGI only and requires the OSC shared texture
to be openable by the game's D3D11 device. DX9, DX12, OpenGL, and Vulkan are not
implemented. No attempt is made to bypass anti-cheat or application protection.
The DLL does not yet support a safe detach command. If enabled, NvContainer
leaves a successfully attached host running until its game exits rather than
force-killing the host while the game may still execute DLL code.

### Current Smash Legends compatibility test

The x64 hook was loaded into a fresh, non-protected Smash Legends D3D11 process
(PID 12236; adapter LUID `00000000:0001353F`) while OSC runs on adapter
`00000000:000143C5`. After restarting the publisher to release its previous
game session's shared handle, the hook registered with the game's ReShade
`dxgi.dll`, logged `Registered ReShade post-overlay compositor`, and opened the
new adapter-matched shared texture. At `presents=3600`, it reported
`draws=1925`, `texture=ready`, `stage=draw-ok`, and `error=0x00000000`. A
transient `Notifier.show` card was visibly composited upright over the live
game. The tested, deployed, and isolated-host hook DLL hashes match.

The overlay shader flips the source V coordinate so CEF's surface appears
upright in the game. The synthetic smoke test now uses different colors in the
top and bottom rows and checks both back-buffer positions, preventing a
regression to vertically inverted composition. That cross-process pixel test
passes. The cross-adapter path now drops frames rather than waiting on a busy
shared-texture mutex; sustained FPS and long-running behavior still need
soak-testing in the live game.

This confirms live in-game composition, not full interactive overlay support:
input, focus, cursor forwarding, resizing, long-running performance, and
cross-adapter readback cost have not yet been characterized. The cross-adapter
path transfers the full CEF surface through CPU-visible staging memory each
frame; same-adapter use avoids that readback. ReShade ordering was verified in
the live ReShade-enabled game: its callback registered and successfully drew
the toast after ReShade's effects. A startup shared-handle failure occurred
against the previous publisher process and recovered after that publisher was
restarted.

## Build and smoke test

Build `NVIDIA OSC Adapter.vcxproj` and
`tests\NVIDIA OSC Adapter Smoke Test.vcxproj` for x64/Release, and build
`NVIDIA OSC Adapter Host.csproj` for x64/Release. Build `NVIDIA OSC.vcxproj`
with the repository's CEF SDK configured to build the texture publisher.
Run `tests\bin\x64\Release\NVIDIA OSC Adapter Smoke Test.exe` with the hook DLL
path to validate hook installation, shared-handle opening, alpha blending, and
the resulting back-buffer pixels in a local D3D11 process. The smoke test runs
the shared-texture publisher in a separate child process and uses a per-process
bridge name through `NVIDIA_OSC_ADAPTER_BRIDGE_NAME`, so it exercises copying a
private source texture into the dedicated shared texture, NT-handle duplication,
keyed-mutex synchronization, pixel visibility, and final composition without
replacing the live OSC publisher's frame. The hook publishes its adapter
identity through a per-process test mapping, so the synthetic run cannot
replace a live game's adapter selection. It also stops the child publisher and
verifies the adapter releases the cached texture.
To inspect the currently published OSC surface without starting the synthetic
publisher, pass `--live-bridge`; this mode opens the production mapping read-only
and draws only into the test process's private swap chain. It also reports the
source PID, bridge/frame dimensions, texture format, alpha range, and bounds of
nontransparent pixels to distinguish a valid but visually empty surface from
actual OSC content. In live-bridge mode it verifies that the consumer's
read-back back buffer contains visible pixels after composition, rather than
accepting the hook's draw counter alone.

The dev build stages the host and DLL together in
`Build\NVIDIA ShadowPlay\NvAdapters`. `oscAdapter.enabled` remains false by
default; use only a controlled, non-protected D3D11 test application while
validating in-game composition and lifecycle.
