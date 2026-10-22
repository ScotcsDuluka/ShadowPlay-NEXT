# Main-menu Group Layout A — V5 draft

Status: isolated model prototype, not enabled in the NVIDIA OSC Build.

## Concepts

- **Panel**: LEFT / CENTER / RIGHT; each holds ordered Groups. There is no legacy 3/4/5 Control limit.
- **Group**: one 240 × 240 native NVIDIA Big-sized design area.
- **Block**: a horizontal strip within Group (Small or Side 240 × 80; Big 240 × 240).
- **Slot**: a sub-area of Block that hosts one original NVIDIA Control. No copied handlers or duplicate keys.
- **Preset**: named widths within a Block. The user selects a preset, then puts controls in its slots.

A Group accepts up to three Small/Side strips (each 80 high), or one Big Block (240 high). Mixed Small + Side strips are supported without an additional Hybrid type. Empty slots are allowed; empty Groups are not persisted.

## Eight starter presets

| Type | Layout | Slot widths | Height | Rendering |
| --- | --- | --- | --- | --- |
| Small | three | 80 / 80 / 80 | 80 | icons |
| Small | wide-left | 160 / 80 | 80 | icons |
| Small | wide-right | 80 / 160 | 80 | icons |
| Small | full | 240 | 80 | icon |
| Side | full | 240 | 80 | icon, text, subtext |
| Side | text-left | 180 / 60 | 80 | detailed left + icon right |
| Side | text-right | 60 / 180 | 80 | icon left + detailed right |
| Big | full | 240 | 240 | icon, text, subtext |

All 12 native controls remain eligible in any Panel and any compatible Slot. Settings is mandatory and movable. No control appears twice.

## Conversion and rollout

- New code: `osc/mods/main-menu-group-layout-a.js`; no runtime integration yet.
- Convert V4 in memory with `migrateV4(v4State)`. Preserve the legacy saved V4 payload untouched.
- Do not assign new keys or styles to NVIDIA native Controls until the editor has a clear preview and migration UX.
- Do not alter the stable V4 Motion or capture/click handling.
- Next step: Settings UI with one Group card, a visual preset selector, drag-target slots, and an Apply/Undo draft stage. The existing running OSC stays on V4 until end-to-end UI tests pass.
