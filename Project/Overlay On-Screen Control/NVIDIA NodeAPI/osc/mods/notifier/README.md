# OSC notifier map

## Load order

`osc/index.html` loads the notifier in this order:

1. `notifier-system/notifier.js` creates `window.Notifier`, owns toast DOM,
   slot routing, groups, entrance/exit, updater dance, and close callbacks.
2. `OSC-Adapter.js` translates standard OSC/OBS events and owns
   `GameWatcherStatus` / ApplicationOntology OpenShare cards.
3. `NvGraphics-Adapter.js` owns `GpuPreferenceApplied` cards and polls
   `gpu/running-profile-paths` to decide whether a running EXE has an applied
   per-app GPU assignment.

All three use the same `window.Notifier`; adapters should not build their own
toast DOM or subscribe to another adapter's server event.

## Event and card ownership

| Input | Adapter / handler | Output |
| --- | --- | --- |
| `notificationEvent` (OSC / app event aggregator) | OSC-Adapter `showNotification` | Registry/localized standard OSC toast |
| `/Duluka/v1/OBSNotification` -> `nv-obs-notification` | OSC-Adapter `showObsNotification` | Whitelisted recording, replay, and broadcast toast; event IDs are deduplicated |
| `/Duluka/v1/GameWatcherStatus` -> `nv-game-watcher` | OSC-Adapter `onGameWatcherStatus` | ApplicationOntology OpenShare toast; game ID + PID + `matchedUtc` identifies one run |
| `/Duluka/v1/GpuPreferenceApplied` -> `nv-gpu-pref` | NvGraphics-Adapter `onPreference` | Explicit GPU preference or System Default toast |
| `/Duluka/v1/gpu/running-profile-paths` (1s poll) | NvGraphics-Adapter `pollNvGraphicsRunningProfiles` | Running-on-GPU toast only for that EXE's explicit applied profile |
| OpenShare `started` / `closed` / `skipped` | Internal `eventAggregator`, OSC -> NvGraphics | Lifecycle barrier only; not server events and not a source of GPU eligibility |

## Shared toast router

Both adapters call `Notifier.show`; `notifier-system/notifier.js` creates and
owns each toast unit and routes it into the configured one-to-three slots.
Priority notifications use a free slot first, then can replace a steady,
non-protected toast. Groups with a `gpu-` prefix are protected from that
replacement path. If every slot is protected or transitioning, the router can
decline a toast; adapters must check that a group actually became active before
considering the notification delivered.

Normal OSC notifications coalesce by their semantic groups (recording, replay,
broadcast, screenshot). OpenShare and running-GPU cards use unique per-run
groups so they are independent cards rather than updates to a shared game
toast.

## Game-start notification flow

```text
NvContainer GameWatcher
  -> NodeAPI /Backend/v.1.0/ContainerEvents
  -> Socket.IO /Duluka/v1/GameWatcherStatus
  -> OSC-Adapter validates catalog match and de-duplicates game instance
       -> one OpenShare toast (EXE + configured shortcut)
       -> nv-openshare-game-card-started / -closed / -skipped (internal only)
  -> NvGraphics-Adapter independently polls running-profile-paths
       -> exact EXE profile + nonempty lastAppliedAdapterId + adapter name
       -> wait if a matching OpenShare toast is active; otherwise show GPU toast
```

The GPU toast is not queued from the OpenShare handler: it is discovered from
NvGraphics running-profile data and only waits on OpenShare while that toast is
actually active. This preserves the ApplicationOntology boundary for the
OpenShare prompt without making the GPU message depend on ontology matching.
An absent or unrenderable OpenShare card therefore cannot suppress the GPU
card.

## NvGraphics toast variants

| Producer | Toast | Eligibility | Updater UI |
| --- | --- | --- | --- |
| NvGraphics-Adapter | `{EXE} is running on GPU` + applied adapter name | EXE appears in NvGraphics running paths and its exact profile has a nonempty `lastAppliedAdapterId` | No |
| NvGraphics-Adapter | `Set {EXE} to use GPU` + adapter name | Fresh `GpuPreferenceApplied`, `isSystemDefault: false`, notifications enabled | Yes |
| NvGraphics-Adapter | `Set {EXE} to use system default` + system-default fallback text | Fresh `GpuPreferenceApplied`, `isSystemDefault: true`, notifications enabled | Yes |

OpenShare is an OSC-Adapter toast, not an NvGraphics variant.

## Duplicate and ordering safeguards

- A GameWatcher instance is keyed by game id, PID, and `matchedUtc`, then
  persisted while the instance remains in the current snapshot.
- OpenShare presentation has a one-second fallback that uses the bundled
  NVIDIA icon while the EXE icon is still loading. It submits exactly one toast,
  avoiding a second `Notifier.show(... updaterUi: true)` call that used to
  create a second toast in a different slot.
- NvGraphics tracks each running EXE path, checks applied preference data
  independently, and de-duplicates its GPU toast until that EXE leaves the
  running-profile list. It resolves the EXE icon from GPU profile data, then
  running-game data, independently of whether OpenShare matched the game. It
  only considers that toast delivered if the group appears in an active router
  slot, so a full set of protected slots can be retried later.
- OpenShare lifecycle events are local `eventAggregator` messages. NvGraphics
  never subscribes to `GameWatcherStatus` or triggers OpenShare.
