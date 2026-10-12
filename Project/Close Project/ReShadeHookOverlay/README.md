# NVIDIA Game Filter Hook Overlay - v0.1

A minimal native ReShade add-on used to prove the in-game overlay path before implementing filter controls.

## What this milestone does

- Builds as a ReShade .addon for x64 and Win32.
- Registers a NVIDIA Game Filter panel in ReShade's in-game overlay.
- Receives ReShade present callbacks and shows a live callback counter.
- Provides a slider and button to verify that UI input works.

## What it does not do yet

- Does not alter the game's colors or apply shaders.
- Does not inject into a game by itself: ReShade must already be installed and enabled for the target game.
- Does not yet use the separate CEF/OSC UI or define a project-wide hotkey.

## Build

Run build.cmd from this folder. Output stays inside this module:

- build\x64\Release\NVIDIA Game Filter Hook.addon
- build\Win32\Release\NVIDIA Game Filter Hook.addon

## Test

1. Build the version matching the game's architecture (x64 for 64-bit games, Win32 for 32-bit games).
2. Place the .addon file in the add-on search directory configured by ReShade. It is commonly the ReShade runtime directory, unless the add-on path was changed in ReShade settings.
3. Launch a game where ReShade is installed and add-ons are enabled.
4. Open ReShade's overlay using the hotkey configured for that installation.
5. Find NVIDIA Game Filter and check that the callback counter increases. Move the slider and press Test interaction to verify input handling.

ReShade versions can differ in add-on API compatibility. The first runtime test should be done in a non-protected/offline test application or a game where add-ons are permitted. This module does not attempt to bypass anti-cheat or other application protections.

## SDK sources

See vendor/SDK-SOURCES.md and the adjacent upstream license files.
