using System;
using System.IO;
using System.Linq;
using System.Text.Json;

// Container is a COMMUNICATION HUB. NvCapture /engines v2 owns the actual
// route, codec and FFmpeg binary capability truth. A fixed policy file may
// constrain routing but must never invent or replace live engine evidence.
internal static class CaptureEngineCatalog
{
    private static bool Bool(JsonElement source, string property) =>
        source.ValueKind == JsonValueKind.Object &&
        source.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.True;

    private static string Text(JsonElement source, string property) =>
        source.ValueKind == JsonValueKind.Object &&
        source.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    private static JsonElement Field(JsonElement source, string property) =>
        source.ValueKind == JsonValueKind.Object &&
        source.TryGetProperty(property, out var value) ? value : default;

    private static string[] Strings(JsonElement source, string property) =>
        Field(source, property) is var array && array.ValueKind == JsonValueKind.Array ?
        array.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).
            Select(x => x.GetString() ?? "").Where(x => x.Length > 0).
            Distinct(StringComparer.OrdinalIgnoreCase).ToArray() : Array.Empty<string>();

    private static bool StringArray(JsonElement source, string property) =>
        Field(source, property) is var array && array.ValueKind == JsonValueKind.Array &&
        array.EnumerateArray().All(value => value.ValueKind == JsonValueKind.String);

    private static bool ValidCaptureRuntime(JsonElement? worker)
    {
        if (!worker.HasValue || worker.Value.ValueKind != JsonValueKind.Object)
            return false;
        var root = worker.Value;
        var configured = Field(root, "configured");
        var ffmpeg = Field(root, "ffmpegBinary");
        var schema = Field(root, "schemaVersion");
        return schema.ValueKind == JsonValueKind.Number &&
            schema.TryGetInt32(out int value) && value == 2 &&
            Bool(root, "readOnly") &&
            Field(root, "recording").ValueKind is JsonValueKind.True or JsonValueKind.False &&
            Field(root, "nativeRuntimeReady").ValueKind is JsonValueKind.True or JsonValueKind.False &&
            Field(root, "nativeRuntimeCodec").ValueKind == JsonValueKind.String &&
            Field(root, "actualSessionBackend").ValueKind == JsonValueKind.String &&
            configured.ValueKind == JsonValueKind.Object &&
            Field(configured, "backend").ValueKind == JsonValueKind.String &&
            Field(configured, "captureApi").ValueKind == JsonValueKind.String &&
            Field(configured, "encoder").ValueKind == JsonValueKind.String &&
            Field(configured, "allowed").ValueKind is JsonValueKind.True or JsonValueKind.False &&
            ffmpeg.ValueKind == JsonValueKind.Object &&
            Field(ffmpeg, "binaryRunnable").ValueKind is JsonValueKind.True or JsonValueKind.False &&
            StringArray(ffmpeg, "captureApis") && StringArray(ffmpeg, "encoders");
    }

    internal static object Snapshot(string configPath, bool runtimeEnabled, bool runtimeReady,
        JsonElement? workerRuntime = null, string workerError = "")
    {
        bool configValid = false;
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
            if (!configValid) configurationError =
                "Capture routing policy does not match the locked Stable route.";
        }
        catch (Exception ex)
        {
            configurationError = "Capture routing policy unavailable: " + ex.GetType().Name;
        }

        bool hasWorker = ValidCaptureRuntime(workerRuntime);
        JsonElement worker = hasWorker ? workerRuntime!.Value : default;
        JsonElement ffmpeg = Field(worker, "ffmpegBinary");
        JsonElement configured = Field(worker, "configured");
        bool nativeReady = hasWorker && runtimeEnabled && runtimeReady &&
                           Bool(worker, "nativeRuntimeReady");
        bool ffmpegBinaryReady = hasWorker && runtimeEnabled && Bool(ffmpeg, "binaryRunnable");

        // No invented GPU, codec, or capture API capabilities: only use
        // names actually returned by NvCapture /engines.
        string codec = Text(worker, "nativeRuntimeCodec");
        string[] nativeEncoders = nativeReady && codec.Length > 0 ? new[] { codec } : Array.Empty<string>();
        string[] nativeApis = hasWorker && Text(configured, "backend") == "Duluka" &&
            Text(configured, "captureApi").Length > 0 ?
            new[] { Text(configured, "captureApi") } : Array.Empty<string>();
        string[] ffmpegEncoders = hasWorker ? Strings(ffmpeg, "encoders") : Array.Empty<string>();
        string[] ffmpegApis = hasWorker ? Strings(ffmpeg, "captureApis") : Array.Empty<string>();
        bool nativeCodecReady = nativeReady && nativeEncoders.Length > 0;
        bool ffmpegUsable = ffmpegBinaryReady && ffmpegApis.Length > 0 &&
                            ffmpegEncoders.Length > 0;
        bool ffmpegHardwareVerified = ffmpegUsable && Bool(ffmpeg, "hardwareEncoderVerified");

        return new
        {
            schemaVersion = 1,
            readOnly = true,
            selectionLocked = true,
            policyValid = configValid,
            policyError = configurationError,
            captureRuntimeEnabled = runtimeEnabled,
            captureRuntimeReady = runtimeEnabled && runtimeReady,
            availability = hasWorker && runtimeEnabled ? "Available" : "Unavailable",
            workerTruthAvailable = hasWorker,
            workerError = hasWorker ? "" : (workerError.Length > 0 ? workerError :
                workerRuntime.HasValue ? "invalid_worker_schema" : "endpoint_unavailable"),
            actualSessionBackend = hasWorker ? Text(worker, "actualSessionBackend") : "",
            recording = hasWorker ? (bool?)Bool(worker, "recording") : null,
            // Missing/invalid policy must not fabricate even a baseline selection.
            // Configured live engine always comes from workerRuntime.configured.
            selected = configValid ? new { captureId = "duluka:dxgi",
                             encoderId = "nvidia:nvenc-h264", policyOnly = true } : null,
            controllerPolicyMatchesWorker = hasWorker && configValid ?
                (bool?)(Text(configured, "backend") == "Duluka" &&
                    Text(configured, "captureApi") == "dxgi_desktop_duplication" &&
                    Text(configured, "encoder") == "NVENC_H264") : null,
            effectiveSelection = hasWorker ? (JsonElement?)configured : null,
            requestedConfiguration = hasWorker && Field(worker, "requestedConfiguration").ValueKind == JsonValueKind.Object
                ? (JsonElement?)Field(worker, "requestedConfiguration") : null,
            savedConfiguration = hasWorker && Field(worker, "savedConfiguration").ValueKind == JsonValueKind.Object
                ? (JsonElement?)Field(worker, "savedConfiguration") : null,
            actualRunningEngine = hasWorker ? Text(worker, "actualRunningEngine") : "",
            verification = hasWorker && Field(worker, "verification").ValueKind == JsonValueKind.Object
                ? (JsonElement?)Field(worker, "verification") : null,
            workerRuntime = hasWorker ? workerRuntime : null,
            workflows = new
            {
                record = new { owner = "NvCapture", transport = "capture_rest",
                    wired = true, runtimeVerified = false },
                replay = new { owner = "NvCapture", transport = "not_verified",
                    wired = false, runtimeVerified = false },
                screenshot = new { owner = "NvCapture", transport = "not_verified",
                    wired = false, runtimeVerified = false }
            },
            providers = new object[]
            {
                new {
                    id = "Duluka",
                    captureApis = nativeApis,
                    encoders = nativeEncoders,
                    codecs = nativeEncoders, // unchanged worker codec keys; do not infer formats
                    routeWired = true,
                    capabilityVerified = nativeCodecReady && nativeApis.Length > 0,
                    hardwareVerified = false,
                    capabilitySource = hasWorker ? "capture_runtime" : "unavailable",
                    runtimeReady = nativeCodecReady,
                    note = nativeCodecReady ? "NvCapture reports an initialized native capture codec." :
                        "Native capture route exists; actual NvCapture encoder not verified ready."
                },
                new {
                    id = "FFmpeg",
                    captureApis = ffmpegApis,
                    encoders = ffmpegEncoders,
                    codecs = Array.Empty<string>(), // worker v2 does not enumerate independent codec IDs
                    routeWired = true,
                    capabilityVerified = ffmpegUsable,
                    capabilitySource = hasWorker ? "capture_runtime" : "unavailable",
                    // A binary scan alone does not prove a GPU or live FFmpeg session.
                    runtimeReady = ffmpegHardwareVerified && runtimeReady,
                    hardwareVerified = ffmpegHardwareVerified,
                    hardwareEncoderVerified = ffmpegHardwareVerified,
                    note = ffmpegUsable ?
                        "Capture-reported FFmpeg binary capabilities; hardware codecs still require a live start test." :
                        "FFmpeg capabilities not yet confirmed by NvCapture."
                },
                new {
                    id = "OBS", captureApis = Array.Empty<string>(),
                    encoders = Array.Empty<string>(), codecs = Array.Empty<string>(),
                    routeWired = false, capabilityVerified = false, hardwareVerified = false,
                    capabilitySource = "unimplemented", runtimeReady = false,
                    note = "OBS integration does not implement the worker recording route."
                },
                new {
                    id = "NVIDIA", captureApis = Array.Empty<string>(),
                    encoders = Array.Empty<string>(), codecs = Array.Empty<string>(),
                    routeWired = false, capabilityVerified = false, hardwareVerified = false,
                    capabilitySource = "unimplemented", runtimeReady = false,
                    note = "Installed NVIDIA recorder ownership is separate from native NVENC. Its recording status is not attested; NvFBC capture is not implemented."
                }
            }
        };
    }
}
