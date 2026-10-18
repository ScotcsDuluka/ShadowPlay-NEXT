# D3D9 Renderer

D3D9 frame-bridge mapping and snapshot, adapter identity publication, texture/state-block resource management, texture upload, and Overlay composition live in D3D9Renderer.cpp. D3D9Hook.cpp delegates Present/Reset events and exported test rendering/status queries through D3D9Renderer.h.

Validation after extraction:
- Native DLL Release x64 build passed.
- D3D9 smoke test passed: present=4, draws=4.
- D3D11 regression smoke test passed: 9 Presents, 6 shared-texture compositions.