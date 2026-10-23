# Phase 6C — RecordingEngine failure-state recovery (offline)

Date: 2026-10-24. Branch: `Stable`. Scope: `Project/NVIDIA Capture`.

## Safe execution boundary

Only the connected AORUS-DESKTOP's local source/build tree was used. **No microphone, system loopback, speaker, screen capture, game injection, or NVENC/D3D11 device initialization occurred.** The test creates an uninitialized-but-Idle `RecordingEngine` via its **real constructor**, and uses reflection to seed its private state for controlled, repeatable fault injection. It deliberately leaves the DXGI capture field null, so `StartSession` fails inside the real orchestrator after the Recording state is entered, before any GPU resources can be created. It requests a minimal disk headroom check but never creates the output MP4. The live NvCapture worker on 59077 is untouched.

## Failure reproduced against the original implementation

The first real-source test run reported:
- PASS: the simulated startup fault produced `SessionResult.ErrorMessage` and returned the State to `Idle`.
- **FAIL**: `RecordingEngine.GetStatus().LastSessionResult` was not updated to the failed session's result. The previous implementation updated `_lastSessionResult` only in the normal `session.Run()` success path, leaving observers unaware of errors. Similarly, `_currentSession` could survive an exception if assigned before `Run()` threw.

## Production fix

`Recording/DulukaEngine.Recording/RecordingEngine.vb` publishes `_lastSessionResult` and clears `_currentSession` from the `Finally` path, whether the session returned normally or threw. Under the same `_sync` lock it decides `Idle` vs `Stopping` based on `_disposeRequested`, and signals `_sessionFinished`. This also avoids a narrow event race where a new session could reset the completion event before an old session signalled it.

This change **reports an actual failure instead of falsely implying recovery**; it does not repair broken NVENC/DXGI devices.

## Device-free regression tests

New C# `EngineCapture/Recovery.Tests` project directly references the production `DulukaEngine.Recording` assembly and invokes real `RecordingEngine.StartSession`, `Stop`, `GetStatus`, and `Dispose` without calling `Initialize`.

It validates:
- Injected startup fault returns failure evidence;
- state reverts to Idle without phantom active session;
- `LastSessionResult` carries the exact returned failure object;
- Stop after failed session is harmless;
- second attempt produces a fresh error and replaces the previous status;
- Dispose transitions to Disposed and refuses another session;
- an artificial already-published session is removed and its failure result is published when the next fault occurs.

**15/15 checks PASS** after the fix; **the test failed before the fix** on the result-publication assertion. The compiled regression was repeated **10 more times (150/150 checks)** with no failures. No actual D3D11/NVENC or WASAPI session was started.

`NvCapture` Release build: **0 errors, 3 pre-existing warnings** from obsolete WebRequest APIs and a ByRef definite-assignment check. No deployed production binary was replaced.

## Remaining acceptance gates

- Actual NVENC driver/encoder crash, GPU device loss, FFmpeg child failure or long-running capture recovery on the user's desktop are **not** certified by this synthetic test.
- Real system/microphone audio and A/V synchronization have not been sampled.
- Offline error-state testing cannot prove frame continuity, GPU resource lifetime or no visible stutter.
- The user must be informed before actual screen capture or modification of OSC UI/OSC transport/NvContainer.

## Reproduce

```powershell
dotnet run --project "C:\My Project\NVIDIA-Shadowplay\Project\NVIDIA Capture\EngineCapture\Recovery.Tests\Recovery.Tests.csproj" -c Release --no-launch-profile
dotnet build "C:\My Project\NVIDIA-Shadowplay\Project\NVIDIA Capture\NVIDIA Capture.vbproj" -c Release -p:Platform=AnyCPU
```
