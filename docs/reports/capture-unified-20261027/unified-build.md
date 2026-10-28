# Unified Capture build and native package evidence

Date: 2026-10-27, Asia/Bangkok. Scope: managed/native build and staging; runtime deployment is owned by the coordinating agent.

## Findings and changes

- `NVIDIA Capture.vbproj` already used real `ProjectReference` entries at Stable `e63b52544a1dd54e51e9d75f2099c808f8a2e392`. The source audit found no stale Release DLL HintPath in the production Capture dependency graph. Its obsolete prebuilt-DLL explanatory comment was corrected.
- `Scripts/build-dev.ps1 -Module Capture` now invokes `Scripts/Build-CaptureUnified.ps1`. This path builds only Capture and its managed project graph, without the unrelated CEF/C++ prerequisite, using a unique SDK artifacts directory. Existing staged files are preserved; a new destination is required for changed builds. `-NoBuild` verifies current source and immutable staged hashes from its own prior manifest.
- Release output is checked against its managed `.deps.json` closure, then staged with the authoritative `Project/FFmpeg` binaries. A SHA256 manifest records all staged files, source digest, build log and explicit `hardwareVerified=false`, `mediaVerified=false`, `deployed=false` values.
- The QSV project accepts an external `QsvNativePackageDirectory` or the default `Native/bin/package`. A complete native package contains `DulukaQsvNative.dll`, `libvpl.dll`, `libvpl-LICENSE.txt`, and `qsv-native-package.json`. Artifact hashes, current bridge/CMake source hashes, pinned dispatcher revision, x64 PE identity, license and the actual ABI 3 export are validated before transitive copy. The ABI export probe creates no capture/encoder device or recording session.
- Optional native-package absence remains a successful NVIDIA managed build with explicit QSV-unavailable output. `-RequireNativeQsv` rejects absence. Partial, stale, wrong-ABI and corrupt packages fail closed. No unverified wildcard runtime DLLs are copied.
- `Build-NativeQsv.ps1` retains the pinned compiler/CMake/Ninja/download recipe, refuses changed dispatcher sources, and emits the reproducible flat package via `New-NativeQsvPackage.ps1`. Native compilation is not required to reuse validated artifacts. Managed QSV initialization already checks ABI and actual adapter identity, and rejects missing DLL/driver/runtime failures.

## Build and fault checks

All local evidence is under `C:\My Project\_CaptureAuditSnapshots\20261027-014248`.

| Check | Result | Evidence |
| --- | --- | --- |
| Independent baseline Capture Release with unique artifacts directory | PASS, 3 existing warnings / 0 errors | `build-baseline.log`, `build-baseline/` |
| Packaging fault matrix under PowerShell 7 | PASS, 12 / 0 | `packaging-fault-checks/checks.json` |
| Same packaging matrix under Windows PowerShell 5.1 `-File` | PASS, 12 / 0 | `packaging-fault-checks-ps51/checks.json` |
| First staging trial during session edits | FAILED, temporary VB reserved identifier | `capture-staging-first.log`; corrected by session owner |
| First two native-required staging attempts | FAILED, Windows PowerShell module/default-parameter behavior | `capture-staging-unified.log`, `capture-staging-unified-v2.log`; validator corrected and direct `-File` ABI probe passed |
| Unified native-required Release v3 | PASS, 2 existing obsolete WebRequest warnings / 0 errors | `capture-staging-unified-v3.log`, `capture-staging-unified-v3/capture-build-manifest.json` |

V3 runtime root: `C:\My Project\_CaptureAuditSnapshots\20261027-014248\capture-staging-unified-v3\NVIDIA Capture`. Both native encoders, all managed dependencies and FFmpeg are staged. V3 predates subsequent media-verifier review fixes; it is a bounded smoke-test artifact, not the final production build.

## Native artifact provenance and transfer

The MateBook native source and CMake files matched the current local Stable source. The dispatcher checkout was clean at `778a66d6c6537f08eabb91955dbbf1bce3812894`. Existing bridge/dispatcher artifacts were packaged after the coordinator's preflight and a separate `packaging-before` snapshot. No native compilation, tool/driver installation or Intel live gate change was needed.

Remote package: `C:\My Project\_CaptureAuditSnapshots\20261027-014830\qsv-native-package`. Local verified package: `C:\My Project\_CaptureAuditSnapshots\20261027-014248\qsv-native-package`.

| File | SHA256 |
| --- | --- |
| `qsv_bridge.cpp` | `00339B5031A10F1AEFD69EE533AAF05AFB9A4FAFA82464B461BE2D826A3FB65D` |
| `CMakeLists.txt` | `09C0491F47205CF4FFC20A25715DEAACA2A7857E1EB4D4D79312ABAEA03B5AD4` |
| `DulukaQsvNative.dll` | `986CB127AB280403D1098D6D3266ACAC63B3A6F936E37E923E1757B6B1E4B543` |
| `libvpl.dll` | `75233A36F1C4FDC82C9CE2E0F8221881516A11C380611E135E31EF28A9F2BE96` |
| `libvpl-LICENSE.txt` | `6B53987F5DE35662CCBCF2753DC84A713068443ACB482FD6FD431FF7CF5DA9F9` |
| Transferred ZIP | `93F87ACCF7553EFB4FA96E4A45E70A57AB9FB66AD926316D2593FC0E1CD09772` |

The transfer used one bounded LAN request, serving only the selected ZIP to the known second host. The listener exited with code 0 after the transfer. The actual ABI export was loaded successfully on both hosts without initializing hardware. This establishes artifact/loader compatibility; it does not establish Intel recording, driver capability or media acceptance.

## Reproduction

```powershell
& '.\Scripts\build-dev.ps1' -Module Capture -Dest 'C:\My Project\_CaptureAuditSnapshots\<fresh-output>' -QsvNativePackageDirectory 'C:\My Project\_CaptureAuditSnapshots\20261027-014248\qsv-native-package' -RequireNativeQsv
& '.\Scripts\Test-CaptureUnifiedPackaging.ps1' -EvidenceDirectory 'C:\My Project\_CaptureAuditSnapshots\<fresh-checks>'
```

No source was staged/committed/pushed, no production process was stopped or overwritten by this workstream, and no screen/audio capture was performed by the build agent. Controlled NVIDIA runtime acceptance is reported by the coordinating agent. The final post-review build and same-source MateBook synthetic acceptance are recorded below when complete.
