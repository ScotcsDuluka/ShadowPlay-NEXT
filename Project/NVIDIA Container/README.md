# NVIDIA Container — orchestration, not a capture engine

**Status: active engineering snapshot, 24 October 2026.** This folder owns the `NvContainer` process, component lifecycle, game discovery, safe routing and local diagnostics. It is **not** responsible for producing video frames, screenshots or encoded files.

## Responsibility and data flow

```text
NvCapture (capture/recording authority) ↔ NvContainer ↔ NVIDIA NodeAPI ↔ OSC UI
ApplicationOntology catalog → NvContainer GameWatcher → authorized OSC Adapter Host → Duluka Hook
```

- **Capture:** GET engines/capabilities/status plus guarded Record Start/Stop and guarded display-selection/screenshot relays. NvCapture owns the result; HTTP 200 on a diagnostics route is not proof that the worker is ready.
- **Game identity:** `GameWatcher` uses a validated local ApplicationOntology cache. Filename match is discovery; verified SHA-256 identity is distinct. Development overrides are **not** a production identity guarantee.
- **Supervisor:** startup/watchdog, process identity/path checks and per-module lifecycle; operating code and deployed state may differ.
- **Security:** loopback request validation mitigates cross-site browser access but does not strongly authenticate every same-user local process. Write routes remain default-deny until trusted IPC and worker compatibility are confirmed.

## Key source and evidence

- `NvContainer.cs`, `Components/Runtime/`, `Components/Diagnostics/`, `Tests/`
- [NVIDIA ShadowPlay Helper](NVIDIA%20ShadowPlay%20Helper/README.md) — hotkey/input ownership; not a capture engine.
- [Component map](Components/README.md)
- [Capture communication contract](CAPTURE-COMMUNICATION-HUB-CONTRACT-20261024.md)
- [Display/screenshot relay](CAPTURE-DISPLAY-RELAY-CONTRACT-20261024.md)
- [Security limits and isolated tests](SECURITY-STABILITY-20261024.md)
- [Pairing secret security](PAIRING-SECRET-SECURITY-20261024.md)
- [Catalog integration](APPLICATION-ONTOLOGY-INTEGRATION.md)

**Recorded test scope:** 26/26 display-relay mock checks and 28/28 Capture Worker Gateway checks in dated reports. These do **not** imply real screen selection or a valid recording. Avoid deploying a new supervisor DLL into a running process; use the [Build/deploy policy](../../docs/BUILD-DEPLOY-POLICY.md).
