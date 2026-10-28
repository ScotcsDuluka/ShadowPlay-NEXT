# CEF 73 bounded compositor patch: build preparation

## Current gate

User authorized the isolated CEF source build, then explicitly authorized installing additional compatible Build Tools. Production deployment remains outside this authorization.

PASS: complete CEF Git checkout at `D:\cef73-build\cef`, detached at `ee4b49f311d5081909d0823244708d9ff157354d`. `CHROMIUM_BUILD_COMPATIBILITY.txt` requires Chromium `refs/tags/73.0.3683.75`. The bounded compositor patch has actually been applied to this checkout: one implementation file, six additions and two deletions. No public headers/ABI changed. `git diff --check` passed.

Correction to the preceding preparation report: its initial apply-check was run in a nested directory inside another Git repository and did not demonstrate application to a real CEF checkout. In the real CEF checkout, the second hunk initially failed because a blank context line was missing. The repository patch was corrected to exact source context; the new verbose check and application both succeeded. `D:\cef73-build\evidence\patch-check.log` records the real check. Earlier measurement results are unaffected.

PASS: portable CPython 2.7.18 from the CPython NuGet package is extracted only under `D:\cef73-build\python2`. It executes successfully. No system PATH, default Python or registry setting was changed.

PASS: the Microsoft Visual Studio 2017 Build Tools bootstrapper was downloaded to `D:\cef73-build\downloads\vs_buildtools_2017.exe`. Authenticode status is Valid, signer Microsoft Corporation. Signature details are saved in `evidence/installer-signature.json`.

FAIL / environment prerequisite: pinned Chromium's actual `vs_toolchain.py` preflight under Python 2.7 fails for both supported versions: Visual Studio 2017 not found; Visual Studio 2019 not found. Installed VS inventory shows only VS 2026, with MSVC 14.44 and 14.51; Windows SDK inventory shows 10.0.26100.0. No unsupported toolchain alias or source workaround was used.

BLOCKED: the normal installer launch was rejected by execution policy (`CreateProcess ... rejected: blocked by policy`) before it ran. The attempted action was launching the verified Microsoft bootstrapper in quiet/no-restart mode with a separate installation path and C++/ATL/SDK components. The combined command's depot_tools clone also did not run. No alternative installer launch mechanism, elevation bypass or policy change was attempted.

Separate public depot_tools/source metadata downloads were attempted normally, but did not complete in the observation window and their owned sessions were interrupted. The partial depot_tools checkout is retained; no source dependency synchronization, GN generation, compilation or native CEF runtime replacement has occurred.

UNVERIFIED: compiled candidate, fresh-image pacing at 75/120/240 under the bounded patch, five-second rate restoration, CPU/GPU load, production OSC/game timing, ReShade, D3D11 and cross-bitness regressions. No new performance numbers are claimed in this stage; prior unlimited-mode numbers are not results for this patch.

## Human prerequisite

Run the signed bootstrapper through the machine's permitted interactive installation workflow. Install VS 2017 Build Tools C++ tools, ATL/MFC and a compatible Windows 10 SDK (the prepared selection is SDK 17763), alongside the existing VS installation. Intended separate installation path: `D:\cef73-build\vs2017`. Do not modify or uninstall the current VS installation. The agent has not executed this installation.

After installation, rerun the saved preflight with `DEPOT_TOOLS_WIN_TOOLCHAIN=0`, `GYP_MSVS_VERSION=2017`, and `vs2017_install` pointing to that installation. Download/synchronize Chromium and a compatible historical depot_tools under the same isolated drive only after that gate succeeds. Keep old source acquisition attempts and logs. Apply the existing CEF checkout's changes when assembling `chromium/src/cef`; do not silently drop them through automate-git updates.

Build x64 first with limited parallelism for the 16 GB RAM host. Package a complete matching runtime under build_test; never replace only installed libcef.dll. Test both timer-driven and requestAnimationFrame pixel probes, with unlimited flags absent. Verify restoration to 75 after the five-second phase and compare producer/Host resource usage before considering production deployment. ReShade/DX11/game regression remains a later explicit runtime gate.

## Evidence

- `D:\cef73-build\evidence\installed-vs.json`
- `D:\cef73-build\evidence\toolchain-preflight.log` and matching exit file
- `D:\cef73-build\evidence\toolchain-2019-preflight.log` and matching exit file
- `D:\cef73-build\evidence\patch-check.log` and matching exit file
- `D:\cef73-build\evidence\installer-signature.json`
- `D:\cef73-build\evidence\preparation-manifest.json`

No production deployment, process restart, game operation, commit or push occurred.
