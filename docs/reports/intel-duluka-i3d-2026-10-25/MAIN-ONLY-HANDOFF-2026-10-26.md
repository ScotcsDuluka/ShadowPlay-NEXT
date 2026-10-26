# ShadowPlay NEXT — Main-Only I3-D Takeover and Acceptance
Date: 2026-10-26 (Asia/Bangkok)
Authority: Main Coordinator (ChatGPT), SINGLE OWNER
Machine: HUAWEI-MATEBOOK
Branch: intel/duluka-capture
Repository: C:\My Project\NVIDIA-Shadowplay
Mailbox: C:\My Project\ShadowPlay-NEXT-AgentBus

## User-directed agent shutdown / consolidation
User explicitly requested: transfer all unfinished work to Main ALONE; A/B/C must cease receiving new assignments. Do not dispatch tasks or invite A/B/C to Claim anything else. Do not restart GPT-6 Astra; user separately stopped that run for token usage.
Original SPN-I3D-001_Astra.working remains PAUSED historical state only. Do not rename to .done or represent it as accepted.
Completed submissions SPN-I3D-002_A.done, SPN-I3D-003_B.done, SPN-I3D-004_C.done are FINAL REPORT ARCHIVE; none should be reclaimed, edited or deleted. Current authoritative mailbox listing showed no pending A/B/C .task or .working task.

## Main decision — I3-D Synthetic/Offline ACCEPTED ONLY
The independent C report has been read and evidence source/log spot-verified by Main; Main accepts the tested *synthetic/offline* implementation scope, not production live safety. Actual independent C results:
- Test suites: I3DWorker 10; I3DFrame 27; B1Worker 3; Video Offline 38 (28 live cases skipped); I2 QSV 659; I3 Transport 660; I3B 331; I3C 285.
- Fresh C total: 2013 PASSED / 0 FAILED / 28 SKIPPED, all exit code 0 as reported. 4 scoped Release builds 0 warnings / 0 errors per C logs.
- Four test-owned synthetic MP4 profiles VBR/CQP x BT.709 limited/full: 60 submitted / encoded / muxed / fully decoded in each; not screen capture or real-time FPS proof.
- Current SHA256s checked directly by Main and MATCH C report:
  DdagrabBackend.Lifecycle.vb D5B669E6277E95B30BA3A0F7B38BFABB7D71E86A45AD1ABEFCF8BB5AA20B377F
  DdagrabBackend.Worker.vb ACA5C0F2597ADC3A30AB9F97DDC35C5A483C439FD6B9DAE412B7A507EB0F0062
  FrameTransaction.vb C5D02FF64B79E53FE573651B54CB1D0113AA8C39587DD07D728CA75AA194FBB4
  D3D11VideoFrame.vb 1CD850120A4B90396890FAF38CAF6F9DF25BD015C89A892B0C29E7AEA0CCBB63
- Main's spot-check of C-evidence logs and hash matches is NOT a new independent execution of all suites.
- Branch confirmed intel/duluka-capture; git status 79 dirty entries; git diff --check exit 0 (two pre-existing line-ending warnings). Preserve every uncommitted entry.
- All unrelated historical I3-B 2056/0/33 and I3-C 1938/0 figures remain historical, not added. Synthetic audio mux 13 checks NOT RUN.

## Outstanding risk and next MAIN-only engineering scope
- B6: BgraSessionSource legacy lifecycle receiver lacks optional generation-stamped fault-sink handling; source-gated faults are tested, receiver-side fail-closed generation check needs a separate, minimal safe compatibility design/test before production wiring.
- B8: IVideoFrameSink that accepts frame then throws before returning PushOutcome has inherently ambiguous ownership. Clarify strict nonthrowing-accept contract / no-accept-on-throw or protocol handshake, with fake negative-case tests before production use.
- B7: Intel shared NT/keyed mutex and foreign native D3D11-device copy paths are intentionally NOT SUPPORTED; no changes authorized to enable them.
- Driver/COM hangs, native handles on real GPU, physical monitor changes, Fullscreen Exclusive, real Desktop/game/audio/mic acquisition, 1080p live throughput, actual long-run crashes, real hardware calibration and I1/full Main Release remain UNVERIFIED.
- Keep intel_native_live_input_not_verified CLOSED. NO deployment, runtime restart, real capture, GPU configuration change, or commit/push without new user authorization.
- Prefer narrow, synthetic-only source changes with baseline preservation. Main owns both implementation and review going forward; no automatic task creation or agent re-activation.

## Status
I3-D SYNTHETIC: MAIN ACCEPTED (bounded scope).
I3-D LIVE INTEL: BLOCKED / NOT AUTHORIZED.
A/B/C: RETIRED FROM ACTIVE TASK ASSIGNMENT BY USER.
ASTRA: PAUSED BY USER.
NEXT OWNER: MAIN ONLY. No background work or future action implied by this file.

## Update — I3-E Main-only implementation, 2026-10-26

Main directly edited 3 files: BgraSessionSource.cs (now implements existing ISourceGenerationLifecycleSink with generation check under recovery gate), QsvNative.Tests/I3B.cs (+6 injected generation-fault tests), and IVideoFrameSink.vb (explicit no accept-then-throw ownership contract). Pre-edit snapshots preserved in docs/reports/intel-duluka-i3e-2026-10-26/main-before/.

Main independently executed scoped Release and synthetic/offline regression: QSV --i3b 337 PASS, QSV --i3c 285 PASS, I3DFrame 27 PASS, Video --offline 38 PASS / 28 SKIP. Total 687 PASS / 0 FAIL / 28 SKIP. Three scoped Release builds 0 warnings and 0 errors. Git diff --check exit 0. No real live DXGI/audio test. Complete report: docs/reports/intel-duluka-i3e-2026-10-26/README.md and main-evidence/.

B6 receiver generation guard: synthetic/offline ACCEPTED with legacy unstamped method retained for compatibility; production sources MUST use stamped variant.
B8: contractual no-accept-then-throw documented, but the ambiguous case remains OPEN pending explicit acknowledgment/protocol solution. Do not claim fully fixed.
Intel LIVE still BLOCKED. A/B/C/Astra remain offline; MAIN remains exclusive owner. No deployment, reset, commit or push.
