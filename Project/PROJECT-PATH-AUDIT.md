# Project path audit

Audit date: 2026-10-12

## NvContainer

The active project is `Project/NvContainer/NvContainer.csproj`. Its source files,
GPU routes, runtime code, diagnostics, and `Assets/NVIDIA ShadowPlay.ico` are
present at the paths used by the project. Both `Project/NVIDIA ShadowPlay.sln`
and `Project/NVIDIA ShadowPlay Dev (no CEF).slnf` reference this project path.
The project references `NvGraphics.Contracts` and `NvGraphics.App`; both
projects exist. NvContainer starts and supervises the NvGraphics UI, so the
existing NvGraphics project reference is retained.

## Legacy solution entries removed from active build lists

The following entries referenced paths that do not exist under `Project`.
They were removed from the active `.sln` and `.slnf` project lists only; no
source, build output, or archived file was deleted or moved.

| Project | Classification | Evidence |
|---|---|---|
| NVIDIA Backend | ARCHIVED | Matching project file is under `Project/Close Project/Overlay ShadowPlay/Backend/`. |
| Launcher | ARCHIVED | Matching project file is under `Project/Close Project/Launcher/`. |
| NVIDIA Controls | ARCHIVED | Matching project file is under `Project/Close Project/Overlay ShadowPlay/Controls/`. |
| nvspcap | ARCHIVED | Matching project files are under `Project/Close Project/NvCapServices/`. |
| Gallery.Video | ARCHIVED | Matching project file is under `Project/Close Project/Overlay ShadowPlay/Gallery.Video.dll/`. |
| NVIDIA ShadowPlay | UNKNOWN | Referenced project file is absent from the active tree and no exact project-file match was found in `Close Project`. |
| NvShim | UNKNOWN | Referenced project file is absent from the active tree and no exact project-file match was found in `Close Project`. |
| NVIDIA Notifier | UNKNOWN | Referenced project file is absent from the active tree and no exact project-file match was found in `Close Project`. |
| libcef_dll_wrapper | UNKNOWN | Referenced native project file is absent from the active tree and no exact project-file match was found in `Close Project`. |
| NVIDIA Share Bootstrap | UNKNOWN | Referenced native project file is absent from the active tree and no exact project-file match was found in `Close Project`. |
| NVIDIA Share | UNKNOWN | Referenced native project file is absent from the active tree and no exact project-file match was found in `Close Project`. |

The UNKNOWN entries are intentionally not redirected to `Close Project`. Their
source provenance and replacement status need additional evidence before
restoring any of them to a buildable solution.

## NvCapture relocation

The tracked `Project/NvContainer/NvCapture` tree is deleted in the current
working tree, and a byte-for-byte copy of all 32 tracked files exists under
`Project/ShadowPlay/NvCapture`. The relocated `NVIDIA Capture.vbproj` builds
standalone in Release with `--no-restore` (5 existing compiler/API warnings,
0 errors); its linked shared sources and local CaptureEngine assembly references
resolve. It is not registered in `NVIDIA ShadowPlay.sln`. No deleted files were
restored and no solution entry was added.

## Game identity and controller boundary

`NvGraphics` and `GpuProfilesRoute` currently persist legacy
per-executable profile records, mixing executable identity fields with GPU
preference fields in `%LOCALAPPDATA%\\NVIDIA ShadowPlay\\NvGraphics\\profiles.json`.
The NvGraphics UI and NvContainer routes both read and write this shape.
Changing the schema or moving data before a verified identity provider and
migration contract exist risks losing the association to existing Windows GPU
preferences.

The NvController executable/source and separate ApplicationOntology Server
repository/client contract are **not yet confirmed from inspectable evidence**
(BLOCKED; this is not a claim that NvController does not exist). The OSC
provisioning payload contains `ApplicationOntology.7z`, whose
`fingerprint.db` is XML with 1,569 game fingerprints, display names, distributor
versions, and executable file names. Its `Ontology64.dll` identifies itself as
the NVIDIA GeForce Experience Application Ontology library and contains game
scanner metadata.
The sibling path `C:\My Project\ApplicationOntology Server` was also checked
and was empty at audit time. Read-only searches of this repository, the
available `OSC-ShadowPlay`, GFE, and `NVIDIA-Plugins` trees, common local
project/download folders, installed NVIDIA directories, and accessible
user-owned GitHub code did not confirm the NvController executable/project or
ontology API/client contract. These search results bound the evidence checked;
they do not prove the project or executable does not exist elsewhere.

### Fact

`GET /Applications/v.1.0/` in
`Project/Overlay OSC/NVIDIA NodeAPI/NvBackendAPI.js` is an NVIDIA GFE Native
Backend route that calls `api.GetApplications()`. Native DLL symbols and the
packaged `ApplicationOntology.7z` show that GFE uses an ontology library and
validates application fields such as `displayName`, `shortName`,
`installDirectory`, and `imageFiles`. This identifies the NVIDIA API; it does
not establish an API contract for the separate ApplicationOntology Server
owned by this project.

Before this audit update, `gpu-adapter.js` requested that NVIDIA route on
startup/game and GPU-preference events and treated its response as the
authoritative game identity list. It also polled GPU-profile and running-game
routes to find candidates. `notifier.js` separately resolves the actual
OpenShare hotkey through OSC's `shadowPlayService` and `keyboardService`; that
is a shortcut lookup, not an application-list/identity lookup.

### Change

`gpu-adapter.js` no longer calls `/Applications/v.1.0/`, GPU manager, or
running-game/profile endpoints, and no longer polls processes or emits
automatic game-open/restart notifications. Until a trusted local Game Identity
provider contract is available, it only shows a GPU-preference toast in
response to the explicit `GpuPreferenceApplied` event. That event is not used
as a game list or game-identity provider. `notifier.js` retains the OSC
OpenShare shortcut lookup so its `arg1` remains the configured key.

Game Identity schema migration and Global Game Watcher ownership in
NvController remain blocked: the separate controller/server source and their
client contract are not confirmed by the evidence checked so far. This
repository must not invent that
contract or host the ApplicationOntology server.

### Hypothesis

The packaged NVIDIA ontology fingerprint data may help identify the GFE
library's internal game catalogue, but it cannot be treated as this project's
identity source without a verified server/client contract and ownership
decision.

### Unknown / BLOCKED

NvController's executable/source location, ApplicationOntology Server
repository, client contract, and a live trusted game-list provider remain
unverified. Automatic game-open notifications and the Game Identity migration
must remain disabled/blocked until those facts are established.
