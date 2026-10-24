# NVIDIA Container — Stability & Security (2026-10-24)

Scope: source-only, isolated build/tests. No product Build deployment, process restart, or running hook detach.

## Implemented

1. **Independent capture worker lifecycle** — `NvContainer.WatchOurs()` now calls `EnsureNvCapture()` on the watchdog cadence whether the OSC/CEF UI is enabled or the NodeAPI health check is passing. `captureRuntime.enabled` still gates startup. This fixes the accidental UI-to-capture startup dependency; it does not claim the recording engine is healthy.
2. **OSC Hook game identity gate** — `OscAdapterLifecycle.StartForGame` checks both exclusive D3D11 metadata and `match.MatchMethod == "sha256"` before starting the injection-capable Adapter host. Discovery by executable name is not trustworthy authentication. Developers may explicitly opt into the previous name-only behavior with `oscAdapter.allowUnverifiedIdentity: true` in `NVIDIA Config/nvcontainer.json`; the default is `false`. Never enable this override for untrusted executables. The verification checks on-disk executable hashes via GameWatcher; it does not independently prove an attached process's in-memory image or runtime graphics API.
3. **Direct control API browser guard** — `ControlRequestSecurity` validates loopback Host and Origin, rejects cross-site Fetch metadata, and requires `application/json` on POST. HTTP duplicate headers and unsupported transfer encoding are rejected; malformed HTTP now produces HTTP 400 where possible. Loopback socket binding is preserved.
4. **NodeAPI GPU proxy browser guard** — `localGpuRequestGuard.js` applies same-origin checks before forwarding `/Duluka/v1/gpu/*` requests to NvContainer (including game restart, preference writes, executable chooser). It retains native loopback callers without Origin.
5. **Hotkey resilience** — Helper now tolerates an absent settings directory during startup and retries FileSystemWatcher installation during periodic settings probes. The default DVR toggle modifier now matches the documented Ctrl+Alt+F10.

## Verification (non-deploy)

- `dotnet run --project "Project/NVIDIA Container/Tests/ControlRequestSecurity.Tests.csproj" -c Release` — 13/13 passing.
- `dotnet run --project "Project/NVIDIA Container/Tests/OntologyIdentity.Tests.csproj" -c Release -- "C:\My Project\nv-ontology-pages\public\catalog.json"` — 20/20 passing.
- `node --test "Project/Overlay On-Screen Control/NVIDIA NodeAPI/tests/localGpuRequestGuard.test.js"` — 4/4 passing.
- `dotnet build "Project/NVIDIA Container/NVIDIA ShadowPlay Helper/nvsphelper.csproj" -c Release --no-restore` — build success, 1 existing obsolete-WebRequest warning.
- `dotnet build "Project/NVIDIA Container/NvContainer.csproj" -c Release -p:Platform=x64 -p:OutputPath=bin/AuditRelease/ -p:AppendTargetFrameworkToOutputPath=false` — isolated build successful; artifacts not deployed into active NvContainer output.

## Deployment warning

The current product configuration observed on 2026-10-24 has `oscAdapter.enabled=true`, `captureRuntime.enabled=true`, and `testMode.enabled=true`. After deploying these source changes, unpinned/only-by-name games will NOT launch the OSC Adapter unless the explicitly unsafe development override is set or catalog SHA-256 pins are provisioned. Plan migration and test known-good games first. Existing injected hook instances must be allowed to exit safely; do not hot-swap running NvContainer DLLs.

## Remaining security/stability limits

- Host/Origin/Fetch validation is **CSRF and DNS rebinding mitigation**, not a strong local authorization mechanism. Any fully trusted same-user local process can forge HTTP headers. Token-bound IPC/ACLs (and an authenticated client migration across NvGraphics/NodeAPI/Helper) require a separately coordinated change.
- Legacy Game Filter menu compatibility endpoints can report support even when the actual Ansel/ReShade runtime is not connected; the real runtime state must be obtained from the adapter, not these menu stubs. No compatibility booleans changed here to avoid a regression.
- No live integration test or hot deploy was attempted. Confirm actual OSC focus, GPU, recording, and Overlay behavior under user supervision.
