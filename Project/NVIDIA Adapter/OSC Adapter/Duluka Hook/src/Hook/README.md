# Hook layer

One folder per graphics API. Hook code owns interception and lifecycle; API-specific GPU resources and composition live in the matching Renderer. Shared DXGI interception helpers belong under DXGI only when their behavior is genuinely API-neutral and verified.

D3D9 and D3D11 composition are now split into src/Renderer/D3D9/D3D9Renderer.cpp and src/Renderer/D3D11/D3D11Renderer.cpp respectively. D3D10, D3D12, Vulkan and OpenGL renderers remain unimplemented.