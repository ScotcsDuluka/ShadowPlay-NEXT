using System;
using System.IO;
using System.Text.Json;

// Read-only view of the *current* capture runtime and four future providers.
// Never equate a provider's source folder or ffmpeg binary with a usable route.
internal static class CaptureEngineCatalog
{
    internal static object Snapshot(string configPath, bool runtimeEnabled, bool runtimeReady,
        JsonElement? workerRuntime = null)
    {
        bool configValid = false;
        bool locked = true;
        string configurationError = "";
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(configPath));
            var root = document.RootElement;
            configValid = root.TryGetProperty("schemaVersion", out var version) &&
                version.ValueKind == JsonValueKind.Number && version.GetInt32() == 1 &&
                root.TryGetProperty("selectionLocked", out var selectionLock) &&
                selectionLock.ValueKind == JsonValueKind.True &&
                root.TryGetProperty("activeCaptureId", out var captureId) &&
                captureId.GetString() == "duluka:dxgi" &&
                root.TryGetProperty("activeEncoderId", out var encoderId) &&
                encoderId.GetString() == "nvidia:nvenc-h264";
            if (!configValid) configurationError = "Capture routing policy does not match the locked Stable route.";
        }
        catch (Exception ex)
        {
            configurationError = "Capture routing policy unavailable: " + ex.GetType().Name;
        }

        // Informational only. The currently deployed NvCapture behavior remains unchanged
        // even if this diagnostic policy is invalid or missing.
        bool ready = runtimeEnabled && runtimeReady;
        return new
        {
            schemaVersion = 1,
            readOnly = true,
            selectionLocked = locked,
            policyValid = configValid,
            policyError = configurationError,
            captureRuntimeEnabled = runtimeEnabled,
            captureRuntimeReady = ready,
            // Locked policy baseline, not necessarily the user's currently requested route.
            selected = new { captureId = "duluka:dxgi", encoderId = "nvidia:nvenc-h264", policyOnly = true },
            workerRuntime,
            providers = new object[]
            {
                new { id = "Duluka", captureApis = new[] { "dxgi_desktop_duplication" },
                    encoders = new[] { "NVENC_H264" },
                    routeWired = true, runtimeReady = ready && configValid,
                    note = "Actual NvCapture Ddagrab + native NVENC; may run only after runtime readiness." },
                new { id = "FFmpeg", captureApis = new[] { "ddagrab", "gdigrab", "gfxcapture" },
                    encoders = new[] { "h264_nvenc", "libx264" },
                    routeWired = true, runtimeReady = false,
                    note = "Existing LegacyCaptureEngine is routed from TCP and record-state poller; require a real FFmpeg runtime probe before claiming ready. Direct HTTP start/stop is not wired." },
                new { id = "OBS", captureApis = new[] { "obs_managed" },
                    encoders = new[] { "obs_managed" },
                    routeWired = false, runtimeReady = false,
                    note = "NvContainer OBS WebSocket integration is for OBS control/status, not worker capture." },
                new { id = "NVIDIA", captureApis = new[] { "nvfbc" },
                    encoders = new[] { "NVENC_H264" },
                    routeWired = false, runtimeReady = false,
                    note = "NVENC encoder exists but NvFBC capture API is not implemented or validated." }
            }
        };
    }
}
