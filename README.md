<!-- SHADOWPLAY NEXT | Flagship ReBuild | Public engineering overview -->
<div align="center">

# SHADOWPLAY NEXT

**Language:** **English** · [ภาษาไทย](README.th.md)

### FLAGSHIP BUILD. **FLAGSHIP REBUILD.**

**THE INTERFACE YOU KNOW. THE POSSIBILITIES YOU DON'T.**

An independent Windows overlay and game-capture platform in the middle of a full architectural rebuild.

[![Status](https://img.shields.io/badge/STATUS-ACTIVE_REBUILD-76B900?style=for-the-badge&labelColor=111111)](https://github.com/ScotcsDuluka/ShadowPlay-NEXT/commits/Stable)
[![Last public commit](https://img.shields.io/github/last-commit/ScotcsDuluka/ShadowPlay-NEXT/Stable?style=for-the-badge&labelColor=111111&color=76B900&label=LAST_PUBLIC_COMMIT)](https://github.com/ScotcsDuluka/ShadowPlay-NEXT/commits/Stable)
[![Development](https://img.shields.io/badge/CHANNEL-CBT1_PREPARATION-20252A?style=for-the-badge&labelColor=111111)](#release-roadmap)
[![Open Beta](https://img.shields.io/badge/OPEN_BETA-01.01.2027-76B900?style=for-the-badge&labelColor=111111)](#release-roadmap)
[![Platform](https://img.shields.io/badge/PLATFORM-WINDOWS_x64-343A40?style=for-the-badge&labelColor=111111)](#requirements-and-compatibility)

**[PROJECT WEBSITE](https://scotcsduluka.github.io/ShadowPlay-NEXT/)** ·
**[LIVE DEVELOPMENT](https://github.com/ScotcsDuluka/ShadowPlay-NEXT/commits/Stable)** ·
**[ISSUES](https://github.com/ScotcsDuluka/ShadowPlay-NEXT/issues)** ·
**[DISCORD](https://discord.gg/v5qUGVD3jZ)**

</div>

> **Capture architecture (ACCEPTED):** [Session-Centric Recording Contract](docs/architecture/SESSION-CENTRIC-RECORDING-CONTRACT.md) — one Duluka recording session, independently configurable Desktop / Fullscreen Exclusive capture APIs and Encoder, shared audio timeline and verified MP4. Some selectable providers are future work; see the contract for current status.

> [!IMPORTANT]
> **Two generations. One evolving vision.** **NVIDIA ShadowPlay OBT3** is the existing legacy release. **ShadowPlay NEXT** is a different, heavily rebuilt development generation — **CBT1 is planned, not publicly released**. The target for **NEXT OBT1 is January 1, 2027** (01.01.2570 in the Thai Buddhist calendar). A target date is not a guarantee of availability.
>
> **Unofficial project.** Not affiliated with, endorsed by, or sponsored by NVIDIA Corporation. NVIDIA, GeForce, ShadowPlay and associated marks are owned by their respective owners. See [Licensing & provenance](#licensing-and-provenance).

---

## This is not just another update

The original project explored the ShadowPlay experience through **VB.NET / Windows Forms**, an independent desktop UI and capture stack.

NEXT is the bigger bet: **CEF-based OSC integration, a native Host, extensible UI modules, renderer / Hook experiments, an evolving Capture engine, game-aware services, and a real cross-process architecture**.

The recognizable NVIDIA-style interface is still part of the experience. Behind it, the implementation is changing fast.

> **OBT3 built the flagship. NEXT rebuilds the flagship.**
>
> *The Git history currently moves faster than the changelog. This README is a curated overview, not a promise that every development branch or local runtime build is already published.*

### One project, two eras

| | NVIDIA ShadowPlay — OBT3 | ShadowPlay NEXT — CBT1 / OBT1 |
|---|---|---|
| Generation | Legacy flagship | New platform / active rebuild |
| UI | VB.NET / WinForms overlay | Original NVIDIA-style OSC resources with CEF / Angular integration and project-specific mods |
| Architecture | Legacy desktop and recording paths | Native OSC Host, NodeAPI, Container, Adapters, Graphics, Capture and Launcher components |
| Customization | Legacy controls | Freestyle V5 layout editor, native-menu integration and game/profile extensions |
| Release state | **OBT3 available** | **CBT1 planned**; **OBT1 targeted for 01.01.2027** |
| Download | [OBT3 release](https://github.com/ScotcsDuluka/NVIDIA-Shadowplay/releases/tag/Release-OBT-3) | **No public CBT1 installer announced** |

<details>
<summary><b>See the legacy overlay</b> — an earlier generation, not a NEXT screenshot</summary>

<br>

![NVIDIA ShadowPlay legacy overlay screenshot](Web%20Github/Assets/overlay.png)

The old UI is part of the project's history. The live NEXT interface and integration tests are changing independently; this image must not be used as proof that a new NEXT build has shipped.

</details>

## Release roadmap

| Milestone | Who gets access? | What it means | Status |
|---|---|---|---|
| **NVIDIA ShadowPlay OBT3** | Public | Existing legacy flagship release | **Available** |
| **ShadowPlay NEXT CBT1** | Existing Duluka Account users with a private invitation | Invitation-only application and manual operator approval; not a public beta download | **Invite-only workflow implemented; general applications closed** |
| **ShadowPlay NEXT OBT1** | Intended public testing | The first open test of the NEXT generation | **Target: 2027-01-01** |
| **ShadowPlay NEXT 1.0** | Public when ready | Release after compatibility, safety and reliability gates | **No date promised** |

**The point of CBT1 is evidence, not hype.** We want repeatable results for overlay lifecycle, input routing, capture integrity, GPU/adapter compatibility, and game-specific behavior **before** expanding access.

**CBT1 invitation and general registration are different states.** An invite-only workflow is documented for **existing Duluka Account users** via [the CBT1 website](https://nv-cbt.pages.dev/), with single-use expiring codes issued privately from the local operator dashboard and manual approval. **General CBT1 applications remain disabled** (`Cbt1:Enabled=false`); the invite path is separately gated. This does not mean a public CBT1 installer is released. See [Server invite rollout](Server/README.md).

**Account login is separate from CBT1 recruitment.** The Launcher source now includes an **Account** button that opens the Duluka Account Portal in an external browser, not inside the CEF Launcher. The current shared-account Portal uses existing accounts; **new public account registration and general CBT1 applications remain disabled**, while the separately gated **invite-only CBT1 application** flow exists for existing account holders. Portal availability or a successful HTTP response is not proof that external sign-in or tester recruitment is fully accepted. See [Server implementation and deployment boundaries](Server/README.md) and [Launcher source status](Project/Launcher.Cef/README.md).

---

## What's being built

**Legend:** `Implemented / testing` means code and selected tests exist; it does **not** mean publicly released or universally compatible. `Experimental` means real-world verification is incomplete. `Planned` is not an implemented feature.

| System | What exists or is being integrated | Engineering state |
|---|---|---|
| **OSC / Native Host** | CEF + Angular interface, native Host integration, NVIDIA-style controls, route/input lifecycle | **Implemented / testing** |
| **Freestyle V5** | Drag-and-drop Main-menu Groups and Controls, Small / Side / Big layouts, Auto-Fill, Undo / Done / Cancel, native `md-menu` and `md-select` | **Implemented / testing** |
| **OSC Motion** | Original-style transitions, preflight layout positioning, native lifecycle integration | **Implemented; open/close QA ongoing** |
| **Game Profiles / GPU preferences** | Per-application executable profiles, names, GPU adapter selection and Windows Graphics preference controls via the NVIDIA Graphics backend; optional Freestyle V5 native Control | **Developer-reported end-to-end laptop test passed: distinct GPUs detected, selection applied, game rendered on the chosen GPU, and preference persisted after restart** |
| **ApplicationOntology game catalog** | Separate Cloudflare Pages catalog and local game-identity cache via `C:\\My Project\\nv-ontology-pages`: executable matching, render-API hints and update notifications | **Local catalog v1.2.9 / 23 entries verified 2026-10-24; public deployment not independently verified; filename matches are not authenticated identities** |
| **Game Filter / ReShade Manager** | OSC Settings includes a guided ReShade Manager: local game-folder inspection, graphics-API hints, evidence-based installation status and official-Setup handoff | **Guided Info/Inspect APIs and guarded JS deployed in a dated 24 October test; no native installation/uninstall writes. Real CEF click-through and in-game ReShade effect still unverified** |
| **DirectX Adapter / Hook** | Native D3D9/D3D9Ex and DXGI/D3D11 code; isolated DX9Ex Host-to-Hook GPU shared-texture IPC, including x86/x64 paths | **Experimental; production Auto-Hook, in-game frame rate and compatibility still require live validation** |
| **NVIDIA Capture** | Capture and encoder work, DXGI monitor enumeration/selection, screenshot engine and Container relay endpoints | **Implemented components and scoped tests; complete live recording/replay and UI integration are not certified** |
| **Encoding & audio** | Native NVENC and FFmpeg engine regimes; WASAPI, timing, mux and integrity validation | **Implementation varies by regime** |
| **NVIDIA Container / Config** | Process lifecycle, game catalog watcher, Capture relay, request security checks and module orchestration | **Active integration; source, staged builds and deployed processes may differ** |
| **Launcher / Modules** | Simplified ShadowPlay ON/OFF and read-only Core Modules catalog (11 built-in components) with local runtime telemetry; Add-ons is presently empty | **Active development: Core cannot be independently edited, disabled or removed. A future Server-owned metadata API is proposed, not connected; legacy appearance tooling is not loaded by the visible quick Core UI** |
| **Gallery / Media** | NodeAPI recent media, safe file copy, metadata and MSE/seek bridge | **Backend regressions passed; actual CEF playback and seeking remain to be tested; delete returns 501** |
| **Duluka Account / tester portal** | Independent account Server with username/password, session/device management and a browser Portal; Launcher has an external Account entry | **Account Portal implemented/tested in isolated cases; shared Portal keeps public account registration and general CBT1 applications closed; private single-use invitation flow can accept an existing-account tester's application. Full external login remains a separate acceptance gate** |

**Current transport and Capture UI (source, 24 October):** D3D9 supports an opt-in **Windowed DWM/DirectComposition GPU preview** and CPU fallback; **D3D9Ex GPU is not currently selectable** through the production Hook transport policy despite isolated cross-process mailbox tests. D3D11 has a GPU option, while D3D10/D3D12/Vulkan/OpenGL native rendering is not implemented. The new **Capture Pipeline Design** panel is size-locked and UI-only: its choices are not persisted or connected to a live encoder. See [Native OSC policy](Project/Overlay%20On-Screen%20Control/NVIDIA%20OSC/README.md) and [Capture UI sizing evidence](docs/UI-CAPTURE-PIPELINE-SIZE-LOCK-20261024.md).

**These are two different systems:** Game Profiles manages per-application GPU preferences in OSC through the local Graphics service. ApplicationOntology provides the published game-identity catalog and catalog updates; it does **not** store or apply a user's GPU profiles. Public catalog: [nv-ontology.pages.dev](https://nv-ontology.pages.dev/) · [ApplicationOntology-API source](https://github.com/ScotcsDuluka/ApplicationOntology-API). The working catalog repository is separate from this tree, and local edits are not automatically public deployments.

The screen shown in the overlay is **not** a compatibility certificate. A visible button, a compiled Hook, or an enabled menu is not proof that the related end-to-end feature is ready for all games.

### Freestyle V5 — make the Main-menu yours

- Arrange Groups across the left, center and right panels.
- Optionally place the NEXT **Game Profiles** native Control alongside the original 12 Controls; availability and live behavior still depend on the local Graphics backend.
- Compose **Big**, **Side** and **Small** Blocks with different slot layouts.
- Drag, swap, insert and reassign Controls without cloning the original NVIDIA menu nodes.
- Keep a single Settings Control; protect layout invariants.
- Preview changes, **Undo**, **Done** or **Cancel** while keeping saved settings intact until confirmation.
- Respect OSC Motion, native control behavior and locale changes.

**Engineering evidence, 2026-10-23:** the selected offline V5 regression suite passed **135 / 135** tests; an earlier combined real-CEF interaction/viewport/lifecycle run passed **18 / 18** checks. **Since those tests, the source catalog gained an optional 13th control, Game Profiles (12 original controls + one NEXT control); the quoted test counts do not certify that newer addition.** A subsequent source-preview audit verified closed/idle native controls but **did not pass a live Native Host activation/open-cycle gate**. These are **dated, scoped results**, not an all-build release certification.

**Read:** [Freestyle V5 QA gate](Project/Overlay%20On-Screen%20Control/NVIDIA%20NodeAPI/tools/FREESTYLE-V5-RELEASE-QA-20261023.md) · [CEF RAF / Host activation audit](Project/Overlay%20On-Screen%20Control/NVIDIA%20NodeAPI/tools/FREESTYLE-V5-CEF-RAF-AUDIT-20261023.md)

---

## Engineering snapshot — 24 October 2026

This is a **dated development snapshot**, not a compatibility matrix or release certificate. Some results below are from isolated source-level tests, while others include explicitly identified runtime checks.

| Workstream | Evidence | Remaining acceptance gate |
|---|---|---|
| **Launcher / read-only Core** | The current quick UI exposes ShadowPlay power, 11 built-in Core module cards, and an empty Add-ons area. Core cards show local status only; data-only appearance tooling remains outside the visible quick UI. | Core metadata is planned to be Server-owned but no verified catalog API is connected. Core users cannot edit settings/layout/backgrounds, disable components independently, or install/remove them as plug-ins; CEF 73 security still needs review. |
| **Game Profiles / Windows GPU settings** | Graphics backend supports per-application preferences; the developer reports a multi-GPU laptop test confirming selected-GPU rendering and persistence after restart. | Validate across more Windows builds, drivers and games; do not present the UI as a full Windows Settings clone. |
| **ApplicationOntology** | Local working catalog `C:\My Project\nv-ontology-pages\public\catalog.json` reports **v1.2.9 / 23 entries** in the 24 October inspection. NvContainer maintains a validated local cache for game matching. | Public API deployment of this local version was **not independently verified**. Executable-name matching alone does not authenticate game identity. |
| **OSC Adapter / Duluka Hook** | D3D9/D3D9Ex and D3D11 native implementations exist. Separate-process tests exercise a **real DX9Ex Host producer and Hook receiver**, including GPU `CopyResource` without intermediate CPU readback and x64-to-x86 test routes. | DX9Ex mailbox remains experimental/opt-in. Live game attachment, frame cadence/FPS, focus/recovery and per-game compatibility remain distinct acceptance tests. |
| **Capture / Container bridge** | DXGI display selection and Screenshot paths are in source. Container owns relay endpoints, **not** display enumeration or screenshot pixels; its recorded relay-mock suite passed **26/26** tests. | Default-denied write routes, exact deployed NvCapture interoperability, CEF UI behavior, real recording/replay and screenshot-save verification must be checked separately. The documented Replay route is **501**. |
| **NodeAPI Gallery** | Bounded nested Recent scanning and checked CopyFile replaced false-success stubs. An MSE video/seek bridge is deployed as a served OSC asset; focused regression recorded **58/58** passes. | Play/pause, seek, A/V sync and long-recording behavior inside the actual OSC CEF are **not yet verified**. Gallery Remove intentionally returns **501**. |
| **Container / NodeAPI security** | Loopback request guards, exact process-path checks, SHA-256 game-identity policy and pairing-secret work have isolated test evidence. | Same-origin/Host checks mitigate browser request abuse but do **not** strongly authenticate every same-user local process. Test real deployed policy before enabling injection or capture writes. |
| **Ansel / ReShade** | Source contracts distinguish the Ansel Adapter boundary from the ReShade Ansel engine, with named-pipe and Game Filter UI integration. | A visible Game Filter menu does not confirm a real runtime effect or shader is active. |
| **Duluka Account / Portal** | Native Launcher source now routes the Account button to an external fixed-HTTPS Portal. The Server provides existing-account login, account/device views and password change with isolated flow tests. | Public registration and general CBT1 applications remain disabled in shared mode; invite-code redemption is separately gated for existing accounts. External end-to-end password login was not automation-verified. The Server's **Core module catalog** remains a separate unimplemented proposal. |

**Architecture and ownership:** `NVIDIA Capture → NVIDIA Container → NVIDIA NodeAPI → OSC UI` for capture control/status; `NVIDIA OSC Host → OSC Adapter / Duluka Hook` for the separate experimental overlay transport. NvCapture is the screenshot/display authority, while Container validates and relays requests.

**Keep these milestones separate:** source modification → focused test → build → deploy/restart → observed runtime → public release. Do not infer one from another. For a code-only README change, no engine restart is necessary.

**Evidence:** [Launcher](Project/Launcher.Cef/README.md) · [OSC Adapter/Hook source status](Project/NVIDIA%20Adapter/OSC%20Adapter/Duluka%20Hook/IMPLEMENTATION-STATUS.md) · DX9Ex producer IPC report (other worktree; unavailable here) · [MateBook Capture architecture](Project/NVIDIA%20Capture/README.md) · Gallery player report (other worktree; unavailable here) · Container security report (other worktree; unavailable here).

---

## HUAWEI-MateBook engineering update — 25 October 2026 (Intel development branch)

This section describes the **local `intel/duluka-capture` worktree on HUAWEI-MateBook**, not the published `Stable` branch or AORUS-DESKTOP runtime. The [accepted Session-Centric Recording Contract](docs/architecture/SESSION-CENTRIC-RECORDING-CONTRACT.md) remains the authority: **one Duluka session, one clock/audio plan, independent video-source and encoder choices, one packet sink/mux**.

| Milestone | Verified on MateBook | Not certified |
|---|---|---|
| Intel I1 / physical target | Intel UHD 8086:9B41, DXGI/CCD source-target identity and guarded routing tests. | Reading monitor metadata is not capturing desktop pixels. |
| Intel I2 native QSV | Owned synthetic NV12 frames → native QSV H.264 → original RecordingSession/MP4 with full-decode VBR/CQP evidence. | CBR remains rejected; hardware support elsewhere is unknown. |
| Intel I3 D3D11 staging | Test-owned D3D11 BGRA → CPU staging → NV12 → same session/QSV/mux; dated I3 suite **1,453 passed / 0 failed / 33 skipped**. | No live Desktop acquisition, device-transition or audio/AV-sync certification. |
| Intel I3 borrowed-frame adapter | New generation-bound `BgraFrameAdapter` validates production-shaped D3D11 frame wrappers and preserves native device/COM ownership. Reported final regression **1,725 passed / 0 failed / 33 skipped** across six successful orchestration stages. | The production Desktop factory is still blocked by **`intel_native_live_input_not_verified`**. Latest composition scope did not rerun 13 historical synthetic-audio checks. |

**Important:** These are synthetic-media and code/driver integration results, **not real-screen recording**, universal Intel GPU support or an installed ShadowPlay NEXT feature. No live pixels, speaker/microphone audio or game data were captured by this documentation update. [Intel Capture module map](Project/NVIDIA%20Capture/README.md) · [I3 frame adapter report](docs/reports/intel-duluka-i3-composition-2026-10-25/README.md) · [I3 synthetic transport report](docs/reports/intel-duluka-i3-2026-10-25/README.md).

---

**MateBook documentation index:** [Intel Capture subsystem](Project/NVIDIA%20Capture/README.md) · [Read/update audit for the MateBook worktree](docs/README-COVERAGE-MATEBOOK-20261025.md). The documentation inventory excludes vendored Intel libvpl dependency READMEs; it does not claim all reports were rebuilt in this edit.

### MateBook I3-B / I3-C proof update — 25 October 2026

The prior I3 and frame-adapter rows above remain **dated milestones**, not the latest work. On the independent `intel/duluka-capture` branch, **I3-B completed its synthetic session/lifecycle/color regression with 2,056 passed / 0 failed / 33 skipped**. A subsequent I3-C synthetic producer/one-session integration was independently checked by Agent C: **1,938 passed / 0 failed / 0 skipped** in its scoped rerun; acceptance is an **Agent C recommendation pending Main review**. The **B1 worker lifetime** guard has synthetic coverage, but a possible final-state race and B2–B8 still require separate review. The current source still rejects live Intel capture via `intel_native_live_input_not_verified`. Native QSV uses a private **ABI 3** bridge and BT.709 Limited/Full verification for the tested synthetic profiles; this is **not** live GPU/game capture or Intel support merged into AORUS.

See [MateBook capture module](Project/NVIDIA%20Capture/README.md) and [two-worktree scope/evidence map](docs/WORKTREE-STATUS-20261025.md). The I3-B and I3-C reports exist only on the MateBook branch and must not be treated as deployed AORUS files.

---

**Browse the first-party documentation:** [documentation index](docs/README.md) · [README coverage inventory](docs/README-COVERAGE-20261024.md).

## Architecture — the rebuild underneath

This is a **conceptual ownership map**, not a claim that every transport is production-ready.

```mermaid
flowchart TB
    User["User / Game / Desktop"] --> Launcher["Launcher.Cef"]
    Launcher --> Container["NVIDIA Container + Config"]
    Container <--> Node["NodeAPI / Service bridges"]
    Node <--> Host["Native OSC Host"]
    Host --> CEF["CEF / Angular OSC"]
    CEF --> Mods["NEXT Mods: Freestyle, Profiles, Filter, Motion"]
    Container <--> Adapter["OSC Adapter"]
    Adapter --> Hook["D3D9 / D3D9Ex / D3D11 Hook + Renderer"]
    Hook -. "experimental frame transport" .-> Host
    Container <--> Capture["NVIDIA Capture"]
    Capture --> GPU["Graphics / GPU identity + frame sources"]
    Capture --> Encoder["NVENC / FFmpeg regimes"]
    Capture --> Audio["WASAPI / A-V timing / mux"]
```

### Capture architecture — control and media paths

Capture uses **two distinct planes**: OSC/NodeAPI/Container pass settings, status and guarded commands; **NvCapture owns the GPU/audio work, output files and true recording state**. The diagram is an ownership map, **not** a claim that every write route is enabled in the installed runtime.

```mermaid
flowchart LR
    OSC["OSC Capture UI"] <--> NODE["NodeAPI :59011"]
    NODE <--> CONT["NvContainer :59012\npolicy + supervision"]
    CONT <--> WORK["NvCapture.exe :59077\nmedia owner"]
    WORK --> BACK{"Capture backend"}
    BACK --> NAT["Duluka: DXGI → native NVENC H.264"]
    BACK --> FF["FFmpeg: ddagrab/gdigrab → encoder"]
    NAT --> MP4["Session result → MP4"]
    FF --> MP4
    AUD["Optional WASAPI system/mic"] -. "timing / mux" .-> MP4
    WORK --> PNG["Selected display → FFmpeg single-frame PNG"]
    WORK -. "actual state / result" .-> CONT
    NODE -. "verified completion events" .-> OSC
```

**Production boundaries:** Capture writes in Container are **default-denied** and require explicit confirmation and compatible worker support; the legacy OSC Record toggle may still have a separate compatibility route. A recorded synthetic NVENC video test is **not** a complete real-screen/native-NVENC recording. Screenshot is independent of Record, and OBS/NvFBC routes remain unavailable/planned. See [full Capture control/media diagrams, backend details and test gates](Project/NVIDIA%20Capture/README.md).

### Where the source lives

```text
Project/
├── Launcher.Cef/                 Desktop / CEF launcher integration
├── NVIDIA Container/             Native supervision and orchestration
├── NVIDIA Config/                Runtime and service configuration
├── NVIDIA Graphics/              Graphics-facing components
├── NVIDIA Capture/               Capture and recording engines
├── NVIDIA Adapter/               API probes, Hook and render adapters
├── Overlay On-Screen Control/
│   ├── NVIDIA NodeAPI/
│   │   ├── osc/                   CEF UI and OSC mods
│   │   ├── tests/                 Node-level and integration regressions
│   │   └── tools/                 QA and controlled source/runtime tools
│   └── NVIDIA OSC/               Native Host
└── FFmpeg/                        FFmpeg-related integration
```

**Source is not Runtime.** The development source tree, local generated builds, installed runtime, and GitHub's public `Stable` branch can be at **different revisions**. Development builds may depend on local prerequisites and are not automatically equivalent to a public release.

### Supported API reality

The current adapter has implemented D3D9/D3D9Ex and D3D11 paths, with different levels of test evidence. Detection of graphics modules is **not** proof of actual rendering support. D3D10, D3D12, Vulkan and OpenGL should **not** be advertised as implemented render paths based on passive probes.

See [Hook implementation status](Project/NVIDIA%20Adapter/OSC%20Adapter/Duluka%20Hook/IMPLEMENTATION-STATUS.md) for dated test results and limitations.

---

## Proof over promises

Some examples from subsystem-specific validation records:

| Evidence snapshot | Observed result | What it **doesn't** prove |
|---|---|---|
| Freestyle V5 Node regression — **2026-10-23** | **135 passed / 0 failed** in the selected suite | Public release readiness |
| Real OSC CEF combined checks — **2026-10-23** | **18 / 18** previously passed | All future runtime builds |
| Later OSC CEF source preview — **2026-10-23** | 12 native controls observed; closed/idle state checked | A successful Native Host open transition |
| Game Profiles multi-GPU laptop check — **developer report, 2026-10-24** | Distinct GPUs detected and selectable; developer confirmed that the game rendered on the chosen GPU and the saved setting persisted after restart | Independent reproduction, all games/drivers/GPUs or performance guarantees |
| D3D11 Hook smoke — **2026-10-22** | **9 Presents / 6 composed frames** in the recorded test | General game compatibility |
| DX9 interactive smoke — **2026-10-22** | Foreground automation blocked that run | A passing real-game DX9 test |

Current focus includes **capture correctness and honest failure reporting**, **GPU adapter/LUID safety**, **host-to-hook synchronization**, **clean OSC motion and input**, and **regressions across game-specific conditions**.

- [Recent code history](https://github.com/ScotcsDuluka/ShadowPlay-NEXT/commits/Stable)
- [Historical build protocol](docs/BUILD_PROTOCOL.md)
- [Capture architecture notes](Project/NVIDIA%20Capture/ENGINE-REALTIME-ARCHITECTURE.md)
- [Repository rebuild plan — proposal, not a completed migration](docs/REPOSITORY-REBUILD-PLAN.md)

> [!WARNING]
> Avoid assuming a new commit is already installed or that a GitHub Pages deployment has been updated. **A code change, a passing test, a deployed runtime and a public installer are four different things.**

---

## Requirements and compatibility

- **Platform:** Windows x64 is the current target family; exact supported versions must be validated per release.
- **GPU / encoder:** Requirements depend on the selected pipeline. **Native NVENC requires a compatible NVIDIA GPU and driver.** FFmpeg-based regimes may provide additional choices where the relevant backend is available and verified.
- **Runtime:** The **legacy OBT3** application targets **.NET 10 Desktop Runtime**. NEXT component prerequisites are still being consolidated and may differ by build.
- **Games:** Anti-cheat, integrity level, graphics API, fullscreen mode, adapter configuration and protected content may affect behavior. No blanket compatibility or protected-content bypass is promised.
- **Performance:** No universal FPS/latency/zero-overhead claim. Publish measurements with hardware, settings and test methodology.

### Downloads

**Looking for something you can install now?** Use the **[legacy OBT3 release page](https://github.com/ScotcsDuluka/NVIDIA-Shadowplay/releases/tag/Release-OBT-3)** and read that release's requirements. It is **not** ShadowPlay NEXT CBT1.

**Looking for NEXT CBT1?** It is in preparation. **No public installer or application page is represented as available here yet.**

Do not install arbitrary development binaries from Git history and assume they are a signed-off test release.

---

## Contributing and engineering discipline

This repository is developing quickly across several simultaneous workstreams. **Please keep changes scoped.**

1. Read the relevant source owner, contract, status file and test instructions first.
2. Use a dedicated branch for external contributions when practical.
3. Commit only files you actually own or intentionally change; never sweep unrelated dirty files into a commit.
4. Run appropriate targeted tests and document scope, environment and limitations.
5. Treat source, staged builds, installed runtime and public release artifacts separately.
6. Open a focused Pull Request with reproduction steps and evidence.

Large dependency or repository moves should follow a reviewed plan, not a bulk rename. See [Repository rebuild plan](docs/REPOSITORY-REBUILD-PLAN.md) and [Historical build protocol](docs/BUILD_PROTOCOL.md).

**Do not submit proprietary NVIDIA assets, extracted binaries, credentials, private user data or unlicensed third-party materials.**

## Licensing and provenance

This is an **independent, unofficial** project. It is **not NVIDIA software**, and it has no claimed NVIDIA endorsement.

The repository includes a [MIT license](LICENSE) and [third-party notices](LICENSE.NOTICE) for applicable project-owned code and attributed dependencies. **A repository-level MIT file does not grant rights to NVIDIA-owned UI assets, other proprietary materials, or third-party components licensed under separate terms.** Check file-level notices and provenance before reuse, distribution or packaging. Public availability is **not** a license to redistribute everything in the tree.

The current architecture incorporates original-style NVIDIA OSC resources and project-specific modifications. The rights to any upstream/proprietary materials must be reviewed independently before including them in distributable CBT1/OBT1 artifacts.

**Trademark notice:** NVIDIA, GeForce, ShadowPlay and their associated visual identities are trademarks of NVIDIA Corporation or their respective owners.

---

## Project and community

Created and developed by **[ScotcsDuluka](https://github.com/ScotcsDuluka)**, with test contributions and feedback from the community, including **ApiwitKaemanee** (legacy QA credit).

- **[ShadowPlay NEXT site](https://scotcsduluka.github.io/ShadowPlay-NEXT/)** — site content is being rebuilt; pages may show earlier releases until updated.
- **[OBT3 legacy site](https://scotcsduluka.github.io/NVIDIA-Shadowplay/obt3.html)** — the previous flagship generation.
- **[Development commits](https://github.com/ScotcsDuluka/ShadowPlay-NEXT/commits/Stable)** — source history, not deployment receipts.
- **[Issues](https://github.com/ScotcsDuluka/ShadowPlay-NEXT/issues)** — bug reports and reproducible cases.
- **[Discord community](https://discord.gg/v5qUGVD3jZ)** — discussion and announcements.

<details>
<summary><b>ภาษาไทย — สรุปแผน ShadowPlay NEXT</b></summary>

<br>

**จาก Flagship Build สู่ Flagship ReBuild**

- **NVIDIA ShadowPlay OBT3:** รุ่นเดิม เปิดให้ใช้งานแล้ว
- **ShadowPlay NEXT CBT1:** รุ่นทดสอบกลุ่มแรก กำลังเตรียมระบบและการเปิดรับสมัคร ยังไม่ประกาศเปิดให้สมัครจริง
- **ShadowPlay NEXT OBT1:** เป้าหมายเปิดทดสอบสาธารณะ **1 มกราคม 2570**
- โค้ดกำลังพัฒนาเร็วมาก ผลทดสอบแต่ละระบบไม่ใช่การรับรองว่า NEXT ทั้งชุดพร้อมแจกแล้ว
- โปรเจกต์นี้เป็นโครงการอิสระ **ไม่ใช่ผลิตภัณฑ์อย่างเป็นทางการของ NVIDIA**

</details>

<div align="center">

---

### **THE FLAGSHIP WAS BUILT. NOW IT'S BEING REBUILT.**

**SHADOWPLAY NEXT · CBT1 IN PREPARATION · OBT1 TARGET: 01.01.2027**

*You've only seen the surface.*

</div>
