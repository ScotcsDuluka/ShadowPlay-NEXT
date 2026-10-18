# D3D11 Renderer

The D3D11 shader/resource/shared-texture composition implementation is in D3D11Renderer.cpp. The Hook calls it through D3D11Renderer.h; exported adapter status functions and the shared frame-bridge contract are preserved. The DXGI/D3D11 smoke test passes after the split.

The renderer has a Shutdown() entry point, but GPU COM-resource release must run before DLL unload on a safe host-controlled path—not from DllMain under the Windows loader lock. The current injected-process lifecycle still needs a host-controlled pre-unload shutdown handshake; process termination remains the OS cleanup boundary.