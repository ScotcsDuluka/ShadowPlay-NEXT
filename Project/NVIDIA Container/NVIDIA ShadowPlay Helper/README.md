# NVIDIA ShadowPlay Helper — hotkey / input service

**Status: active development (24 October 2026).** This small `.NET` helper is owned by the Container subsystem. It handles global hotkey state and related runtime configuration; it is **not** a capture engine, GPU renderer or OSC web page.

- `NvHotkeyHelper.cs` — current helper behavior and configuration-watch lifecycle.
- `nvsphelper.csproj` — build target.
- Runtime policy is supplied through `NVIDIA Config/nvsphelper.json` and other owner-managed settings.

The helper must not forward game/overlay hotkeys when a modal settings editor is actively capturing a shortcut; focus and Alt+Z interactions require real desktop acceptance tests. Recent source changes added resilience to missing settings directories and retrying watcher installation, but those checks do not prove every hotkey path works correctly after a restart.

Read [Container orchestration](../README.md), [active Config inputs](../../NVIDIA%20Config/README.md) and [Build/deploy policy](../../../docs/BUILD-DEPLOY-POLICY.md). Avoid a runtime restart for documentation-only updates.
