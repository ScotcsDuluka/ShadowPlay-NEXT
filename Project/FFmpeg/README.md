# FFmpeg — integration location

**Status: integration directory, 24 October 2026.** This folder is a designated location for FFmpeg-related dependencies or utilities in the development layout. The inspected directory contained no direct top-level project files or binaries. **Do not treat a directory name as proof FFmpeg is installed, bundled, runnable or licensed for redistribution.**

Actual encoder and mux availability is determined by the active Capture worker, the configured binary path, codec negotiation and runtime tests. FFmpeg process orchestration and recording sources also live in [Duluka Capture](../NVIDIA%20Capture/EngineCapture/Duluka%20Capture/README.md).

Third-party FFmpeg distribution and codec licensing must be checked independently before packaging a release.
