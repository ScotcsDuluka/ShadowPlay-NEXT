# NVIDIA Graphics — per-application Windows GPU preferences

**Status: implemented components, verification still in progress (24 October 2026).**

This subsystem discovers GPUs, manages application profile settings and integrates Windows GPU preference controls. It is **distinct from ApplicationOntology game identity** and from the DX9/DX11 overlay Hook.

## Source owners

- `NvGraphics.App/` — Windows UI, app-level services and runtime GPU monitoring.
- `NvGraphics.App/Services/WindowsGpuPreferenceService.cs` — Windows GPU preference write/read integration.
- `NvGraphics.App/Services/GameProfileStore.cs` — application profile storage.
- `NvGraphics.App/Services/GpuDiscoveryService.cs` and `GpuTelemetryService.cs` — adapter inventory/telemetry.
- `NvGraphics.Contracts/` — shared protocol models.
- `NvGraphics.Tests/` and `NvGraphics.UiSmoke/` — isolated regressions and UI smoke checks.

## What a result proves

A recorded developer test on a multi-GPU laptop verified separate GPU detection, actual game rendering on the selected GPU, and persistence after restart. It **does not** guarantee all games, drivers or Windows variants, or prove that the UI clones every capability of Windows Graphics Settings.

Keep selected/preferred GPU separate from measured runtime GPU; read-back and restart persistence are different acceptance checks. Avoid claiming DX9 Hook render support or successful capture based on a GPU profile selection. UI changes must respect the backend's existing permissions and identity checks.

See [repository architecture](../../README.md) and [Container components](../NVIDIA%20Container/Components/README.md).
