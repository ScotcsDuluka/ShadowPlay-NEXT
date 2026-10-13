# ApplicationOntology integration in NvContainer

## Role and scope

NvContainer remains the supervisor process. The Game Watcher uses ApplicationOntology as the authority for Game Identity. NvGraphics remains a GPU preference/profile manager and is not coupled to Game Identity.

Default public API: https://nv-ontology.pages.dev/api/

The ApplicationOntology catalog is the allowlist for process-name candidates. NvContainer loads the current catalog version and catalog document, validates both, and only checks interactive-session processes whose executable name appears in the catalog. It then asks POST /v1/games/resolve to confirm the candidate. Resolution results (including unknown/ambiguous results) are cached per executable name until the catalog changes. A catalog-version/name change invalidates decisions and stops Hook Overlay instances managed by this lifecycle controller.

When catalog refresh fails, NvContainer does not add new game matches. Previously matched processes remain tracked until they exit or the catalog changes. The watcher skips protected/inaccessible processes instead of guessing paths.

## Local loopback diagnostics

The existing control API remains loopback-only (default 127.0.0.1:59012). It is not a public listener.

- GET /v1/ontology/status — checks public/local API health and catalog version.
- POST /v1/ontology/resolve — forwards one JSON resolve request.
- GET /v1/game-watcher/status — watcher state, catalog version/count, candidate process count, cached resolutions, active matches, last scan/refresh times, and errors.
- GET /v1/osc-adapter/status — OSC Adapter lifecycle configuration and any adapter host processes launched by NvContainer. The older `/v1/hook-overlay/status` path remains as an alias.

## OSC push event (available before render hook)

NvContainer publishes a snapshot through the existing loopback-only NodeAPI `ContainerEvents` route as Socket.IO event `/Duluka/v1/GameWatcherStatus`. The event envelope contains `schemaVersion`, `eventId`, `publishedUtc`, and `status`; the status carries catalog health and the current matched game list (PID, executable path/name, canonical game ID, display name, and catalog version). It is emitted when state changes and at a 15-second heartbeat, rather than every scan. NodeAPI validates the schema and rejects snapshots older than two minutes.

This is game-detection metadata only. It does not mean an OSC render hook is attached or running, and it does not start capture.

Example checks (PowerShell):

    Invoke-RestMethod http://127.0.0.1:59012/v1/ontology/status
    Invoke-RestMethod http://127.0.0.1:59012/v1/game-watcher/status
    Invoke-RestMethod http://127.0.0.1:59012/v1/osc-adapter/status

Resolve example:

    $body = @{ executablePath = 'C:\Games\Example.exe' } | ConvertTo-Json
    Invoke-RestMethod -Method Post -Uri http://127.0.0.1:59012/v1/ontology/resolve -ContentType 'application/json' -Body $body

## Configuration

In NvConfig/nvcontainer.json:

- applicationOntology.baseUrl: default public URL. APPLICATION_ONTOLOGY_API_BASE_URL can override it.
- applicationOntology.timeoutSeconds: request timeout (clamped to 2–15 seconds).
- gameWatcher.enabled: enables the watcher (default true).
- gameWatcher.pollIntervalSeconds: process scan interval (clamped to 1–15 seconds).
- gameWatcher.catalogRefreshSeconds: catalog refresh interval (clamped to 10–300 seconds).
- gameWatcher.maxConcurrentResolutions: bounded concurrent resolves (clamped to 1–8).
- oscAdapter.enabled: default false; enable only after controlled-app compatibility and safe detach behavior are reviewed.
- oscAdapter.executable: adapter host executable path relative to Build root. Absolute paths and paths escaping Build root are rejected.
- oscAdapter.workingDirectory: working directory relative to Build root.
- oscAdapter.stopGraceSeconds: time to allow an adapter host process to close before termination.

## OSC Adapter lifecycle and DXGI hook

NvContainer is the lifecycle owner: when enabled and the configured adapter host executable exists inside Build, it starts one x64 host for each ApplicationOntology-matched x64 game and passes:

- --game-id <canonical-game-id>
- --game-name <display-name>
- --game-pid <pid>
- --game-executable <absolute-path>

The host verifies the target PID and executable path, then loads the x64
`NVIDIA OSC Adapter Hook.dll` into the game. The native module hooks
`IDXGISwapChain::Present` and reads the CEF shared-texture handle published by
`NVIDIA OSC.exe` through a versioned local mapping. When ReShade is loaded, the
module registers for ReShade's `reshade_present` add-on event and composites
the OSC surface after ReShade's post-processing effects; otherwise, it uses
the DXGI Present fallback. Both paths restore the game's D3D11.1 context state.
The publisher gates game frames on the mapped game PID being the foreground
process. It hides the desktop OSC window without activation while the game is
focused (focus is polled independently of CEF paint callbacks) while keeping
the CEF windowless browser unhidden so its paint loop is not throttled by the
hidden desktop window. Closing OSC publishes a zero-frame sentinel, which
makes the game hook skip its cached image; cross-adapter readback is also
skipped until OSC opens again, when a full frame is republished. It then
restores desktop OSC and publishes the same zero-frame sentinel when focus
leaves. On focus return, the publisher invalidates CEF and forces one full
snapshot so the game hook cannot reuse the pre-focus image. The cross-adapter
publisher uses a three-slot asynchronous readback ring and skips frames when
the GPU copy or shared-texture mutex is not ready,
so it does not wait synchronously for each frame. Clicks in registered OSC UI
regions and clicks across the screen while modal OSC is open are forwarded to
CEF. Keyboard capture is limited to the open modal UI and released on close or
focus change, preserving gameplay input while OSC is closed; Windows shortcuts
remain unconsumed. The local D3D11 smoke test verifies rendered back-buffer pixels and that a
zero-frame sentinel hides the cached image until a visible frame returns; it
does not verify live focus switching or input. DX9, DX12, OpenGL, and
Vulkan are not yet implemented. The `oscAdapter.enabled` config remains false
by default.
Because a safe in-process detach command is not implemented yet, NvContainer
does not kill an adapter host that is still attached to a live game; it lets
that host finish when the game exits.

## Current verified limits

As of 2026-10-12, the public production ApplicationOntology catalog is version 1.1.0 with seven entries. Identity resolution currently matches executable basenames; no production SHA-256 hashes have been verified, so this is not cryptographic process authentication. The watcher only considers accessible processes in the interactive session and the catalog allowlist.

The full Game Watcher path has been integration-tested against synthetic process names. The OSC push event is schema-validated and keeps Hook Overlay disabled. The .NET local Server also exposes GET /v1/catalog/download/{version} so the watcher can use the same health/version/download/resolve workflow locally; unknown versions return 404 catalog_version_not_found.
