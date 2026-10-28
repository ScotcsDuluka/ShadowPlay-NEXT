# Actual CEF frame delivery verification — 2026-10-25, v1

User request: verify whether frames are actually sent at the configured rate, after raising GPU requests above 60.

## Result

**FAIL — fresh image delivery did not attain requested 75/120/240 FPS in these isolated CEF tests.** Increasing the CEF setting increased accelerated callback counts, but actual GPU texture pixel serials changed at approximately 60 images/second. The preceding ceiling change passes request-policy/build checks; it is not certified as a fresh-frame-rate fix.

**PASS — measurement validity:** actual CEF GPU callbacks and actual GPU pixels were sampled, all handles were valid, all measured GPU callbacks had pixel coverage, no sample-buffer overflow or backwards pixel serial was recorded, and no CPU OnPaint fallback occurred. Each completed fixture exited 0 (data capture complete; this is not a performance PASS).

**UNVERIFIED:** production OSC Present rate under the new build, full Host five-second boost-task-chain integration, unique images reaching the display, game FPS/1% Low/P95/P99, live UI/ReShade and the exact internal compositor/VSync/BeginFrame mechanism responsible. No new Host was deployed or launched.

## Test isolation and binaries

New `NVIDIA OSC/tests/CefFrameDeliveryTest.cpp` and `.vcxproj` run a private windowless CEF browser on a hidden fixture-owned HWND, without game attachment, Hook loading, OSC ports/shared mappings, input hooks, user configuration or visible Overlay. No Windows Computer Use was resumed.

The isolated and installed libcef.dll SHA256 values match exactly:
`1B547411E093468639C98D8E9D83A2F57A7F19A9A6895A3A966A9F7269E7E73A`.

Installed OSC Host remains unchanged:
`A2A778C40FD84987101637A43046F291FB718301E5BF42E9894AEFFF4C0EA136`.

The probe uses current source FPS policy helpers and directly invokes the real CEF setter at 75, 120 and 240; its getter reports 60 in every phase, consistent with the pinned CEF 73 getter's separate legacy clamp. Getter values are not used to infer actual image rate.

Five phases per run: 75 for 10 s, 120 for 10 s, 240 for 10 s, 240 for 5 s, restoration to 75 for 10 s. Initial warm-up is 3 s, with 500 ms settling after each setter call. Actual phase lengths use QPC; the five-second timer-driven phase lasted 5.0017011 s. This tests direct CEF source requests/restoration, not an actual game mode change.

## Pixel proof

The local 640×360 page encodes a monotonically increasing 24-bit counter as its opaque background RGB. The probe copies one pixel from the real CEF shared GPU texture inside its accelerated callback, using a ring of 16 staging/query slots. Readback polling uses DONOTFLUSH and DO_NOT_WAIT; no render-thread wait is used while recording. A bounded final drain occurs outside each timed phase. Copies/flushes add probe overhead; an initial callback-only run produced essentially the same callback counts (about 135, 180 and 300/s).

Two workloads were run:

1. requestAnimationFrame changes the counter/color on every animation callback.
2. A 4 ms JavaScript interval changes the counter independently of requestAnimationFrame. GPU-observed counter spans were 2498, 2492 and 2500 across the first three ~10 s phases, and 1250 across the ~5 s phase. Thus the second workload changed its source color around 250 times/second, while the sampled output exposed only around 60 distinct images/second.

All measured GPU callbacks in both pixel runs obtained a decoded serial. Counts below are image transitions in callback order. The short monotonic test does not wrap the 24-bit counter. No frame deduplication or throttling was added to production code for this verification.

## Timer-driven workload: actual results

| Phase | Requested | GPU callbacks/s | Pixel samples | Distinct images | Repeated-image callbacks | Distinct images/s | Result |
|---|---:|---:|---:|---:|---:|---:|---|
| Desktop 75 | 75 | 135.0708 | 1351 | 601 | 750 | 60.0870 | FAIL |
| Desktop 120 | 120 | 179.9960 | 1800 | 601 | 1199 | 60.0987 | FAIL |
| Source 240 | 240 | 299.8874 | 2999 | 601 | 2398 | 60.0975 | FAIL |
| 240 for five seconds | 240 | 299.6980 | 1499 | 301 | 1198 | 60.1795 | FAIL |
| Restore 75 | 75 | 134.9917 | 1350 | 601 | 749 | 60.0963 | FAIL |

The requestAnimationFrame workload also delivered approximately 60 distinct GPU images/s in all five phases: 60.0929, 60.0947, 59.9997, 59.9811 and 59.9960. This avoids attributing the second result solely to a requestAnimationFrame input clock.

The probe criterion is explicitly observed distinct-image count / actual QPC phase duration within ±5% of the request. CEF's requested maximum is not a guaranteed delivered rate; FAIL here means the requested delivery target was not attained in this workload/environment.

### First-observed unique-pixel transition timing

These are intervals between callback timestamps associated with newly observed GPU pixel serials. They are not GPU execution-completion timestamps, displayed frametimes or game frametimes; asynchronous readback and callback sampling contribute to observation timing.

| Timer-driven phase | Interval-average image FPS | Image 1% Low | P95 interval ms | P99 interval ms |
|---|---:|---:|---:|---:|
| 75 | 60.04 | 35.57 | 27.42 | 27.79 |
| 120 | 60.05 | 39.25 | 18.60 | 24.83 |
| 240 | 60.03 | 49.10 | 17.92 | 18.53 |
| 240 / five seconds | 60.11 | 51.92 | 18.21 | 19.10 |
| Restore 75 | 60.09 | 35.72 | 27.15 | 27.66 |

Interval-average FPS = 1000/mean(unique-transition intervals); 1% Low = 1000/mean(largest ceil(N×0.01) intervals); percentiles use nearest rank. Count/window FPS in the previous table differs slightly due to phase-boundary counting. No long interval is removed.

## Interpretation and next gate

Confirmed: CEF accepts higher GPU settings into a path whose callback cadence changes, but extra callbacks frequently expose the same image. A high callback or Present count is insufficient evidence of high fresh-image FPS. The original host's actual delivered image rate remains separately unmeasured at the new settings.

The next investigation must isolate CEF/Chromium content composition and BeginFrame/VSync scheduling, with these pixel-serial tests as the acceptance test. The present evidence does not identify a universal immutable CEF 60-FPS ceiling, prove that DWM or a driver caused it, or establish the cause of the original game stutter. No speculative CEF binary patch, scheduling flag or production change was applied.

## Evidence

Repository-relative root: `build_test/NVIDIA CefFrameDelivery_Test/20261025-attempt1/`.

- `capture/`: initial callback-only run, `callbacks.csv`, `phases.csv`, `delivery-summary.json`.
- `capture-pixels/`: requestAnimationFrame pixel run, raw CSV and `delivery-summary-with-timing.json`.
- `capture-timer-pixels/`: 4 ms timer pixel run, raw CSV and `delivery-summary-with-timing.json`.
- `hashes-before.json`, `pixel-fixture-hash.json`, `timer-fixture-hash.json` and `verification-manifest.json` pin binaries/sources.
- `run.stdout.txt`, `run-pixels.stdout.txt`, `run-timer.stdout.txt`, matching exit files and CEF logs preserve each execution.
- `callback-only-fixture.exe` and `raf-pixel-fixture.exe` preserve earlier tested binaries. Current `runtime/CefFrameDeliveryTest.exe` is the timer-capable fixture.
- `build-attempt1.log` preserves an initial API-signature compilation error. Corrected builds passed; warnings came from CEF headers. No failed evidence was overwritten.

`tests/Analyze-CefFrameDelivery.ps1` refuses to overwrite summaries, checks callback/phase accounting and full pixel coverage, distinguishes repeated callbacks from image transitions, and emits explicit data-quality/target statuses. Earlier summaries remain immutable when the analyzer gains timing fields.

No deployment, commit, push, live-game operation or modification to installed CEF/OSC binaries occurred.
