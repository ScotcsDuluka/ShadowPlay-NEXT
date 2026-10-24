# NVIDIA Config — runtime policy inputs

**Status: live configuration boundary; inspect deployed copies separately (24 October 2026).**

This directory contains configuration *sources*. The active service reads a staged copy under `Build/NVIDIA ShadowPlay/NVIDIA Config/`, which can differ from both this folder and GitHub `Stable`.

## File ownership

- `nvcontainer.json` — NvContainer supervision, GameWatcher, OSC Adapter, Capture gateway and safety flags.
- `capture-engines.json` — Capture backend policy/catalog; policy presence alone is not worker readiness.
- `nvnode/` and `webhelper.json` — NodeAPI/Web Helper integration inputs.
- `nvidia-osc.json` — OSC behavior.
- `nvsphelper.json` — Helper/hotkey behavior.
- `notifier_obs.json` — notifications.

## Safety

Never commit credentials, pairing secrets, active session tokens or personally identifying runtime data. Do not enable development identity overrides or Capture write commands merely to make a UI button appear available. A default-denied route must remain denied until all components share the compatible contract and authorization policy.

Use [Container contract](../NVIDIA%20Container/README.md) and [Build/deploy policy](../../docs/BUILD-DEPLOY-POLICY.md). Back up deployed policy before a controlled change and preserve user configuration; source change is not runtime activation.
