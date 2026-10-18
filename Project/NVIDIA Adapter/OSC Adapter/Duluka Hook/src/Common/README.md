# Shared native infrastructure

Keep API-neutral types, logging contracts and inter-process transport here. The existing shared frame-bridge header remains in include/osc_frame_bridge.h for compatibility during this structural pass.

A transport contract can be shared; API-owned GPU resources and synchronization objects cannot be assumed to be interchangeable. Each Renderer must own its device-specific resource lifecycle and expose a narrow lifecycle/frame interface to the adapter.