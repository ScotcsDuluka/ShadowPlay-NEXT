# Project NodeAPI — NVIDIA Web Helper host

This managed host is built from `NVIDIA Web Helper.vbproj` and staged beside
the active `Project\Overlay OSC\NVIDIA NodeAPI` payload as
`Overlay OSC\NVIDIA NodeAPI\NVIDIA Web Helper.exe` plus its `.dll` body.
The host runs that tree's `NvNode.exe index.js`; it does not launch the
nested `NVIDIA Web Helper\Backend` project.

## What it is (evidence, not assumption)

The genuine installed NVIDIA `NVIDIA Web Helper.exe` (GFE 3.28.0.412,
`NvNode`) is an embedded Node.js v11.13.0 win32-ia32 runtime with a HARDCODED entrypoint
(`<PFx86>\NVIDIA Corporation\NvNode\index.js`); it discards external argv
and cannot be repointed (proven by forensics: the Phase 1B launch contract,
`forensics/runtime-forensic.txt` in the NVIDIA-Shadowplay repo, and
`Backend/node-runtime/` as the immutable reference). It is an
NVIDIA-specific Node launcher, NOT a generic one. This Project keeps its
own managed host and NodeAPI runtime together in the Build Tree.

The separate clean-room backend sources under this host project's `Backend\`
are not part of the active Build Tree staging contract.

## Host contract (the 9 lane goals)

| # | Goal | Where |
|---|------|-------|
| 1 | Project host / EXE | `NVIDIA Web Helper.vbproj` → `NVIDIA Web Helper.exe` |
| 2 | production name | `<AssemblyName>NVIDIA Web Helper</AssemblyName>` (exe + body dll) |
| 3 | installed owner-layout, not repo cwd | everything resolves from `AppContext.BaseDirectory` (the deployed `Overlay OSC\NVIDIA NodeAPI\`) |
| 4 | backend on port 59011 | spawns `NvNode.exe index.js` with cwd = installed NodeAPI; port = env `NVSP_PORT` > `config.json` > 59011 |
| 5 | stdout/stderr/logging ชัด | console + `Logs\NVIDIA Web Helper.log` beside the exe; child lines tagged `[NODE]`/`[NODE!]` |
| 6 | duplicate-process guard | named mutex (`Global\NvBackend.WebHelperHost.<dirhash>`, `Local\` fallback) → exit 0; port guard: healthy backend already up → exit 0 idempotent; foreign port holder → exit 1, no doomed spawn |
| 7 | graceful shutdown | Ctrl+C (`e.Cancel` + grace window so node's own SIGINT handler runs) / `ProcessExit`; job-terminate fallback |
| 8 | health/proof check | polls `GET /Backend/v.1.0/health` up to 60s → `PROOF:` boot log line; child death before proof = explicit error exit |
| 9 | dependency resolution from active NodeAPI | runtime resolution: `NVBACKEND_NODE_EXE` env > `NvNode.exe` > `node.exe` > PATH > known Node install paths; JS deps resolve from `NodeAPI\node_modules` |

No-orphan guarantee: the child is assigned to a Windows job object with
`JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE` — the backend cannot outlive the host,
even on a hard kill or host crash. Safe, because the backend is crash-safe
by design (uncaughtException → keep serving; atomic tmp+rename store).

## Exit codes

```
0   ran and stopped cleanly / duplicate instance / backend already healthy
1   port held by a process that is not a healthy backend
2   node runtime not resolved
3   index.js missing (incomplete Project NodeAPI tree)
4   node could not be started / exited before the health proof
5   boot window elapsed without health proof (child kept alive, supervised)
N   backend exited on its own with code N (propagated)
```

## Config contract

JSON + environment ONLY (launch contract §5 — the backend's argv is
discarded by the real Web Helper, so ours ignores it too; no registry).
Resolution order mirrors `Backend\config.js`: defaults < `config.json`
(`port`, `host`) < env (`NVSP_PORT`, `NVSP_HOST`). Health path:
`/Backend/v.1.0/health`.

## Staging

`Scripts\build-dev.ps1` builds this host and stages it beside the Project
NodeAPI files in the Build Tree. `Project\Launcher.Cef\deploy-launcher.ps1`
is a compatibility wrapper for that same canonical build-and-stage script.

## Out of scope (by design)

- Restart policy: a crashed backend exits the host with the child's code;
  restart authority stays with NvContainer.
- The separate clean-room parity backend under `Backend\` is not launched
  by this host.
