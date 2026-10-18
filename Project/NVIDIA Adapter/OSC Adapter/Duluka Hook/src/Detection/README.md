# Graphics API Detection

**Status: planned — no runtime detector is implemented here yet.**

The detector should report per-process runtime evidence for D3D9, D3D10, D3D11, D3D12, Vulkan and OpenGL. A game name or executable name is not enough: a process can initialize multiple graphics APIs, launchers may differ from the actual renderer process, and compatibility layers may affect the observed path.

Required future contract:
- Return Detected, Unsupported, or Unknown, plus API, process ID, evidence and timestamp.
- Prefer observed modules, factories, devices and presentation interfaces over static title mappings.
- Never silently route an unsupported API through the D3D11 renderer.
- Keep detection separate from Hook installation so failed detection is diagnosable.