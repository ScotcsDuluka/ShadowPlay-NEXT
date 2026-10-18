# Renderer layer

Each supported graphics API owns a separate Renderer. Keep device/resource state, composition, API-specific synchronization and teardown here. Hook code calls the matching Renderer through a small lifecycle/frame contract.

D3D9 and D3D11 composition have been extracted into their own translation units. D3D10, D3D12, Vulkan and OpenGL are planned directories only; their Hook and Renderer implementations are not present yet.