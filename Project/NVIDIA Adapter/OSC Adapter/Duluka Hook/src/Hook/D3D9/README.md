# D3D9 Hook

D3D9Hook.cpp owns D3D9/D3D9Ex factory import patching, device-vtable Present/PresentEx and Reset/ResetEx interception, hook removal, and exported test/status entry points. Frame-bridge reads, D3D9 texture/state-block resources, CPU texture upload and composition live in src/Renderer/D3D9/D3D9Renderer.cpp.