# D3D11 Hook

D3D11Hook.cpp owns DXGI Present interception, ReShade add-on registration, Hook lifecycle, diagnostics export and forwarding to the renderer. D3D11 shader creation, shared-texture handling, render-state preservation, bridge consumption and composition live in src/Renderer/D3D11/D3D11Renderer.cpp.

D3D11 is still the only implemented DXGI/D3D11 path; this does not establish D3D10 or D3D12 support.