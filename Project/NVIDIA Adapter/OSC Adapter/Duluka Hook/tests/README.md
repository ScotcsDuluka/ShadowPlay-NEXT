# Native Hook tests

Existing smoke-test sources and Visual C++ projects are intentionally kept at their current paths during this structural pass so that local invocation paths are not silently broken.

- d3d9_smoke_test.cpp / NVIDIA OSC Adapter D3D9 Smoke Test.vcxproj: D3D9 and D3D9Ex composition path.
- smoke_test.cpp / NVIDIA OSC Adapter Smoke Test.vcxproj: DXGI/D3D11 Present, bridge and composition checks.

After the project builds from the moved source paths, rerun both smoke tests. Passing a standalone smoke test is not the same as proving that a real game uses the matching API path.