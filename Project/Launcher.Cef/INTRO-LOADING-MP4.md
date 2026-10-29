# Launcher.Cef — GitHub Loading.mp4 Opening Page (2026-10-29)

## Source
- GitHub repo `ScotcsDuluka/ShadowPlay-NEXT`, branch `Stable`, file at repository root `Loading.mp4`.
- Git blob `f38d99f78209ce02005a49eb5dae19dd14d750f5`; bytes `503616`.
- Original video: 2560×1440, H.264 (High)/AAC, 60fps, ~2.54 seconds.
- Original byte-for-byte copy: `ui/media/Loading.mp4` (verified Git hash).

## CEF 73 compatibility
- OSC and Launcher use shared CEF 73 `libcef.dll`. Actual read-only CEF probe returned:
  `video/mp4 = maybe`, `H264 = ''`, `AAC = ''`, `VP8 WebM = probably`.
- The launcher therefore loads `ui/media/Loading.webm` first (VP8, 1280×720, 60fps, **video-only**) and retains `Loading.mp4` as the original/fallback source. This was converted **offline**, not as a runtime dependency. No FFmpeg integration in Duluka Capture.
- Native `launcher_http_server.cpp` now serves `video/webm` and `video/mp4` MIME types.
- Both media assets ship in the existing `ui/` bundle deployment.

## Intro UI
- HTML video overlay opens before the Quick Launcher dashboard, `object-fit:contain` preserves aspect ratio, no crop.
- Silent autoplay, skip button (also Escape), progress bar.
- On `ended` / `error` / autoplay failure / 3-second startup timeout / 7-second absolute guard, remove intro and reveal existing Launcher UI.
- Existing RPC, native modules, Quick Launcher, language, account and power operations are untouched.

## Validation / deployment
```powershell
$base='C:\My Project\NVIDIA-Shadowplay\Project\Launcher.Cef'
node --check "$base\ui\intro.js"
node "$base\tests\intro-video.test.cjs"
node "$base\tests\quick-ui.test.cjs"
node "$base\tests\command-center-ui.test.cjs"
node "$base\tests\languageCoverage.test.cjs"
$env:CEF_ROOT='C:\My Project\cef-sdk\cef73'
& "$base\deploy-launcher.ps1" -NoBuild
```
The deployment script builds/stages Launcher-only resources with backups; **do not restart or overwrite the shared CEF 73 runtime**.

### Real renderer result — AORUS-DESKTOP (2026-10-29)

- Native Release build succeeded for `Launcher.vcxproj` and `Launcher Bootstrap.vcxproj` (CEF 73 SDK).
- Launcher-only deploy succeeded: `LAUNCHER_STAGE_PASS: 7 changed artifacts`, backed up under `Build/DeploymentBackups/launcher-20261029-215620-967`.
- Started real product Launcher with `--no-supervise --self-exit-ms=14000 --remote-debugging-port=59357` (no Capture/OSC actions).
- CDP observed currentSrc `http://127.0.0.1:52703/media/Loading.webm`, readyState=4, error=null, duration=2.517, and progression from `playing` to `ended`.
- `window.__launcherIntroState`: `{started:true, finished:true, reason:'ended'}` and `body.className === 'quick-mode'`, with Quick Launcher native controls present.
- Tested CEF 73 directly: MP4 H264/AAC unsupported, VP8 WebM `probably`; source MP4 retained unchanged.
- Intro lifecycle regression + existing Quick UI/Command Center/language tests passed.

The intro fade (`intro-leaving`) finishes 400ms after video ends. No production CEF runtime files were modified.
