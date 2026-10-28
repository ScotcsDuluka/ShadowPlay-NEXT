# CEF 73 fresh-frame investigation and bounded candidate

## Outcome

PASS: the installed-version CEF binary can produce more than 60 distinct GPU images/s. With `--timer-animation --disable-frame-rate-limit`, the isolated pixel fixture delivered approximately 250 fresh images/s. This is a measured workaround, not a production fix.

FAIL: the unlimited workaround does not respect 75/120 FPS requests or restoration to 75. It must not become a default Host switch. Disabling GPU VSync alone still delivered approximately 60 fresh images/s at every setting.

UNVERIFIED: the bounded CEF source candidate, its binary build, production OSC/game delivery, Desktop resource usage, ReShade and DX11 regression. No production Host, SDK binary, Hook or game was changed or restarted. No deployment/commit/push occurred.

## Reproducible evidence

Root: `build_test/NVIDIA CefFrameDelivery_Test/20261025-scheduler-attempt1/`.

The fixture remains a hidden private 640x360 CEF browser. The local page changes an RGB serial every 4 ms. Actual shared GPU pixels identify fresh images; callbacks alone are not treated as new images. It uses the same libcef hash as the preceding test and production. `hash-manifest.json` records source, test executable, pinned upstream source and installed binaries. Installed Host/libcef fingerprints remain those in the preceding delivery report. Both fixtures completed capture and graceful shutdown; no fixture process remained afterward. Captures preserve all intervals and full pixel coverage passed.

The fixture now accepts explicit allowlisted test switches after its capture directory. Browser and subprocess command lines use Chromium's normal propagation. Commands were run sequentially against the same fixture executable and isolated runtime:

```
CefFrameDeliveryTest.exe <new-capture-directory> --timer-animation --disable-frame-rate-limit
CefFrameDeliveryTest.exe <new-capture-directory> --timer-animation --disable-gpu-vsync
```

Raw `callbacks.csv`, `phases.csv`, CEF logs, stdout/stderr and `delivery-summary.json` are under `capture-unlimited/` and `capture-vsync/`. The build log is retained. The earlier unmodified baseline is preserved under `20261025-attempt1/capture-timer-pixels/`.

| Request / phase | Unlimited distinct images/s | VSync-only distinct images/s |
|---|---:|---:|
| 75 | 249.76 | 60.09 |
| 120 | 249.95 | 60.09 |
| 240 | 249.97 | 59.99 |
| 240 / 5 seconds | 249.96 | 60.17 |
| Restore 75 | 249.98 | 60.09 |

For the unlimited 240 phase, first-observed unique-pixel interval Average = 249.98 FPS, 1% Low = 141.29 FPS, P95 = 5.84 ms, P99 = 6.66 ms. These describe this probe's GPU image observation, not displayed frames or game frametimes. This does not certify steady 240 delivery. Other phase timing metrics and data-quality accounting are in the JSON files.

## Mechanism and candidate

Pinned CEF commit: `ee4b49f311d5081909d0823244708d9ff157354d`, Chromium 73.0.3683.75.

CEF's `SetFrameRate` updates its own BeginFrame timer to the requested shared-texture rate, but leaves compositor VSync configuration as a TODO for issue 2517. `SubmitCompositorFrame` explicitly describes changing the synthetic source from its default 60 Hz, but that update is also a TODO. Thus a higher request is not propagated to this other scheduling stage. The experiment removing BeginFrame limiting strongly supports this scheduler bottleneck; it does not prove every detail of the GPU driver's behavior.

Sources:

- https://github.com/chromiumembedded/cef/blob/ee4b49f311d5081909d0823244708d9ff157354d/libcef/browser/osr/render_widget_host_view_osr.cc#L630-L643
- https://github.com/chromiumembedded/cef/blob/ee4b49f311d5081909d0823244708d9ff157354d/libcef/browser/osr/render_widget_host_view_osr.cc#L1558-L1604
- https://github.com/chromium/chromium/blob/73.0.3683.75/components/viz/common/switches.cc#L34-L36
- https://github.com/chromium/chromium/blob/73.0.3683.75/ui/compositor/compositor.h#L281-L285

Prepared `NVIDIA OSC/tests/cef73-osr-vsync-candidate.patch` updates the two missing sites using that exact Chromium version's `Compositor::SetDisplayVSyncParameters`, with the existing requested interval. It changes internal implementation only, with no CEF public API or Hook ABI changes. Initial setup and resynchronization after the first compositor frame are both covered. It deliberately does not add an unlimited flag.

PASS: `git apply --check` against the downloaded exact pinned source. UNVERIFIED: compilation and runtime effects, including whether later GPU VSync feedback overrides the interval. This repository has binary SDKs (`cef73`, `cef138`), not a full pinned Chromium build checkout. Changing application headers or linking the same binary cannot implement this internal fix. No source build, SDK replacement, binary patch or CEF upgrade was attempted or claimed.

Next acceptance gate: build this candidate in a separate pinned CEF 73 source checkout, package the complete matching runtime only under build_test, and rerun both rAF and timer pixel probes. Require requested 75/120/240 rates and five-second restoration without unlimited mode, then test the native Host integration and actual game regressions. Do not deploy the candidate before those checks and explicit user authorization.
