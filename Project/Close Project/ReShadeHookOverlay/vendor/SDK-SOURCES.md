# Vendored header sources

The SDK directory contains header-only API declarations required to compile the add-on. No third-party implementation binaries are included.

- ReShade headers: https://github.com/crosire/reshade/tree/main/include
- Dear ImGui headers: https://github.com/ocornut/imgui/tree/v1.92.5-docking

The ImGui files are from the v1.92.5-docking ref. ReShade headers were fetched from main on 2026-10-12 and declare RESHADE_API_VERSION 20. The ReShade main ref is moving; confirm compatibility against the target ReShade runtime during the first in-game test.

License texts are stored in LICENSE-RESHADE.md and LICENSE-IMGUI.txt.
