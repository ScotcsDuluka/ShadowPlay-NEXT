#nullable enable
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

// NvCapture owns all recording state, encoders and capture sessions.
// This class only probes and forwards explicitly permitted, bounded loopback requests.
internal sealed class CaptureWorkerGateway
{
    internal sealed record Probe(JsonElement? Data, string Code, int UpstreamStatus, string Message)
    {
        internal bool Success => Data.HasValue && Code == "";
    }

    private readonly HttpClient _http;
    private readonly string _root;
    private readonly bool _enabled;
    private readonly bool _allowRecordCommands;
    private readonly bool _testMode;
    private readonly bool _allowDisplayCommands;
    private readonly bool _allowScreenshotCommands;
    private readonly Func<TimeSpan> _elapsedSinceStartup;
    private readonly Func<string?> _workerWriteToken;
    private int _hasObservedReadyWorker;
    private int _commandInFlight;
    private int _startOutcomeUnresolved;
    private int _stopOutcomeUnresolved;

    internal CaptureWorkerGateway(HttpClient http, int port, bool enabled, bool allowRecordCommands,
        bool testMode = false, bool allowDisplayCommands = false,
        bool allowScreenshotCommands = false,
        Func<TimeSpan>? elapsedSinceStartup = null,
        Func<string?>? workerWriteToken = null)
    {
        if (port < 1 || port > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        _http = http;
        _root = "http://127.0.0.1:" + port;
        _enabled = enabled;
        _allowRecordCommands = allowRecordCommands;
        _testMode = testMode;
        _allowDisplayCommands = allowDisplayCommands;
        _allowScreenshotCommands = allowScreenshotCommands;
        var clock = Stopwatch.StartNew();
        _elapsedSinceStartup = elapsedSinceStartup ?? (() => clock.Elapsed);
        _workerWriteToken = workerWriteToken ?? (() => null);
    }

    internal Probe Read(string endpoint)
    {
        if (!_enabled) return new(null, "runtime_disabled", 0, "Capture runtime is disabled.");
        if (endpoint is not ("health" or "state" or "engines" or "displays"))
            return new(null, "invalid_endpoint", 0, "Unsupported capture read endpoint.");
        return Exchange(HttpMethod.Get, "/" + endpoint, null, TimeSpan.FromSeconds(3),
            value => endpoint == "displays" ? ValidDisplays(value) : ValidRead(endpoint, value));
    }

    private Probe Exchange(HttpMethod method, string path, string? body, TimeSpan timeout,
        Func<JsonElement, bool> validate)
    {
        try
        {
            using var cancellation = new CancellationTokenSource(timeout);
            using var message = new HttpRequestMessage(method, _root + path);
            if (method != HttpMethod.Get)
            {
                string? token;
                try { token = _workerWriteToken(); }
                catch { return new(null, "worker_authorization_unavailable", 0,
                    "Capture worker authorization is unavailable."); }
                if (token == null || token.Length != 32 || !token.All(Uri.IsHexDigit))
                    return new(null, "worker_authorization_unavailable", 0,
                        "Capture worker authorization is unavailable.");
                message.Headers.Add("X-ShadowPlay-Capture-Token", token);
            }
            if (body != null)
                message.Content = new StringContent(body, Encoding.UTF8, "application/json");
            using var reply = _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead,
                cancellation.Token).GetAwaiter().GetResult();
            int status = (int)reply.StatusCode;
            if (reply.StatusCode == HttpStatusCode.NotFound)
                return new(null, "endpoint_not_found", status, "NvCapture endpoint is not deployed.");
            if (!reply.IsSuccessStatusCode)
                return new(null, "upstream_http_error", status, "NvCapture returned HTTP " + status + ".");
            if (reply.Content.Headers.ContentLength > 128 * 1024)
                return new(null, "invalid_response", status, "NvCapture response exceeds 128 KiB.");
            using var stream = reply.Content.ReadAsStreamAsync(cancellation.Token).GetAwaiter().GetResult();
            using var bounded = new MemoryStream();
            var block = new byte[8192];
            while (true)
            {
                int n = stream.ReadAsync(block.AsMemory(), cancellation.Token).AsTask()
                    .GetAwaiter().GetResult();
                if (n == 0) break;
                if (bounded.Length + n > 128 * 1024)
                    return new(null, "invalid_response", status, "NvCapture response exceeds 128 KiB.");
                bounded.Write(block, 0, n);
            }
            using var document = JsonDocument.Parse(bounded.ToArray());
            var value = document.RootElement;
            if (value.ValueKind != JsonValueKind.Object)
                return new(null, "invalid_response", status, "NvCapture response must be a JSON object.");
            if (Text(value, "error") == "not_found")
                return new(null, "endpoint_not_found", status, "NvCapture endpoint is not deployed.");
            if (!validate(value))
                return new(null, "invalid_response", status, "NvCapture response failed schema validation.");
            return new(value.Clone(), "", status, "");
        }
        catch (OperationCanceledException)
        {
            return new(null, "timeout", 0, "NvCapture request timed out; command result may be unknown.");
        }
        catch (HttpRequestException)
        {
            return new(null, "unavailable", 0, "NvCapture endpoint cannot be reached.");
        }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException)
        {
            return new(null, "invalid_response", 0, "NvCapture returned invalid or incomplete data.");
        }
    }

    private static bool IsBool(JsonElement value, string name) =>
        Field(value, name).ValueKind is JsonValueKind.True or JsonValueKind.False;
    private static bool IsString(JsonElement value, string name) =>
        Field(value, name).ValueKind == JsonValueKind.String;
    private static JsonElement Field(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var field)
            ? field : default;
    private static string Text(JsonElement value, string name) =>
        IsString(value, name) ? Field(value, name).GetString() ?? "" : "";
    private static bool Bool(JsonElement value, string name) =>
        Field(value, name).ValueKind == JsonValueKind.True;
    private static bool ArrayOfStrings(JsonElement value, string name) =>
        Field(value, name) is var field && field.ValueKind == JsonValueKind.Array &&
        field.EnumerateArray().All(x => x.ValueKind == JsonValueKind.String);

    private static bool ValidRead(string endpoint, JsonElement root)
    {
        if (endpoint == "health") return IsBool(root, "ready") && IsBool(root, "recording");
        if (endpoint == "state")
        {
            if (!IsBool(root, "ready") || !IsBool(root, "recording") ||
                !TryInt32(Field(root, "elapsedSec"), out var seconds) || seconds < 0)
                return false;
            // The installed legacy NvCapture /state has no owner field.
            // Accept it ONLY when the complete legacy signature is present.
            // Never infer a recording backend from enabled/failReason.
            if (IsString(root, "actualSessionBackend")) return true;
            return Field(root, "actualSessionBackend").ValueKind == JsonValueKind.Undefined &&
                IsBool(root, "enabled") && IsString(root, "failReason") &&
                IsString(root, "outputPath") && IsString(root, "lastFile");
        }
        var schema = Field(root, "schemaVersion");
        var configured = Field(root, "configured");
        var ffmpeg = Field(root, "ffmpegBinary");
        return schema.ValueKind == JsonValueKind.Number &&
            schema.TryGetInt32(out var version) && version == 2 &&
            Bool(root, "readOnly") && IsBool(root, "recording") &&
            IsBool(root, "nativeRuntimeReady") && IsString(root, "nativeRuntimeCodec") &&
            IsString(root, "actualSessionBackend") &&
            configured.ValueKind == JsonValueKind.Object &&
            IsString(configured, "backend") && IsBool(configured, "allowed") &&
            IsString(configured, "captureApi") && IsString(configured, "encoder") &&
            ffmpeg.ValueKind == JsonValueKind.Object &&
            IsBool(ffmpeg, "binaryRunnable") &&
            ArrayOfStrings(ffmpeg, "captureApis") && ArrayOfStrings(ffmpeg, "encoders");
    }


    private static bool TryInt32(JsonElement value, out int result)
    {
        result = 0;
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out result);
    }

    private static bool TryUInt32(JsonElement value, out uint result)
    {
        result = 0;
        return value.ValueKind == JsonValueKind.Number && value.TryGetUInt32(out result);
    }

    private static bool ExactDisplayId(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object) return false;
        return TryInt32(Field(item, "OutputIndex"), out var output) &&
            output >= 0 &&
            TryUInt32(Field(item, "AdapterLuidLow"), out _) &&
            TryInt32(Field(item, "AdapterLuidHigh"), out _);
    }

    private static bool ValidDisplays(JsonElement root)
    {
        var version = Field(root, "schemaVersion");
        var displays = Field(root, "displays");
        var selected = Field(root, "selected");
        return TryInt32(version, out var number) && number == 1 &&
            Bool(root, "readOnly") && Text(root, "source") == "NvCapture" &&
            displays.ValueKind == JsonValueKind.Array &&
            displays.EnumerateArray().All(ExactDisplayId) &&
            IsBool(root, "selectionValid") && IsBool(root, "recording") &&
            IsBool(root, "appliesOnEngineRebuild") && IsString(root, "error") &&
            (selected.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ||
                ExactDisplayId(selected)) &&
            (!Bool(root, "selectionValid") ||
                (ExactDisplayId(selected) && displays.EnumerateArray().Any(d =>
                    Field(d, "OutputIndex").GetInt32() == Field(selected, "OutputIndex").GetInt32() &&
                    Field(d, "AdapterLuidLow").GetUInt32() == Field(selected, "AdapterLuidLow").GetUInt32() &&
                    Field(d, "AdapterLuidHigh").GetInt32() == Field(selected, "AdapterLuidHigh").GetInt32())));
    }

    private static object RelayError(Probe result) => new
    {
        availability = "Unavailable",
        code = result.Code,
        error = result.Code,
        upstreamStatus = result.UpstreamStatus
    };

    private static int RelayErrorStatus(Probe result) =>
        result.Code == "endpoint_not_found" ? 501 :
        result.Code == "timeout" ? 504 : 503;

    internal (int Status, object Body) DisplayInventory()
    {
        var result = Read("displays");
        return result.Success ? (200, result.Data!.Value) :
            (RelayErrorStatus(result), RelayError(result));
    }

    // The worker is the only owner of display selection or screenshot bytes.
    // This gateway only validates the caller, checks live worker evidence and
    // forwards a fixed JSON schema over the configured loopback connection.
    internal (int Status, object Body) DisplayCommand(bool screenshot, JsonElement request)
    {
        if (!_enabled) return (503, new { saved = false, availability = "Unavailable",
            code = "runtime_disabled" });
        if (_testMode) return (409, new { saved = false, code = "test_mode_active" });
        if (screenshot ? !_allowScreenshotCommands : !_allowDisplayCommands)
            return (403, new { saved = false, code = screenshot
                ? "screenshot_gateway_disabled" : "display_selection_gateway_disabled" });
        if (request.ValueKind != JsonValueKind.Object || !Bool(request, "confirmed"))
            return (400, new { saved = false, code = "confirmation_required" });

        int output = 0;
        uint low = 0;
        int high = 0;
        if (screenshot)
        {
            if (request.EnumerateObject().Count() != 1)
                return (400, new { saved = false, code = "invalid_screenshot_request" });
        }
        else
        {
            if (request.EnumerateObject().Count() != 4 ||
                !TryInt32(Field(request, "outputIndex"), out output) || output < 0 ||
                !TryUInt32(Field(request, "adapterLuidLow"), out low) ||
                !TryInt32(Field(request, "adapterLuidHigh"), out high))
                return (400, new { saved = false, code = "invalid_display_selection" });
        }

        // Shared command gate with RecordStart/Stop blocks accidental races.
        if (Interlocked.CompareExchange(ref _commandInFlight, 1, 0) != 0)
            return (409, new { saved = false, code = "capture_command_in_flight" });
        try
        {
            var health = Read("health");
            var state = Read("state");
            if (!health.Success || !state.Success)
                return (503, new { saved = false, code = "capture_health_unavailable" });
            if (Bool(health.Data!.Value, "recording") || Bool(state.Data!.Value, "recording"))
                return (409, new { saved = false, code = "capture_busy" });
            if (!Bool(health.Data!.Value, "ready") || !Bool(state.Data!.Value, "ready"))
                return (503, new { saved = false, code = "capture_runtime_not_ready" });

            var displays = Exchange(HttpMethod.Get, "/displays", null,
                TimeSpan.FromSeconds(3), ValidDisplays);
            if (!displays.Success) return (RelayErrorStatus(displays),
                new { saved = false, availability = "Unavailable", code = displays.Code });

            var inventory = displays.Data!.Value;
            if (Bool(inventory, "recording"))
                return (409, new { saved = false, code = "capture_busy" });
            if (!Bool(inventory, "selectionValid") && screenshot)
                return (409, new { saved = false, code = "capture_display_unavailable" });
            if (!screenshot && !Field(inventory, "displays").EnumerateArray().Any(item =>
                Field(item, "OutputIndex").GetInt32() == output &&
                Field(item, "AdapterLuidLow").GetUInt32() == low &&
                Field(item, "AdapterLuidHigh").GetInt32() == high))
                return (400, new { saved = false, code = "capture_display_invalid" });

            string body = screenshot ? "{\"confirmed\":true}" :
                JsonSerializer.Serialize(new { confirmed = true, outputIndex = output,
                    adapterLuidLow = low, adapterLuidHigh = high });
            var result = Exchange(HttpMethod.Post, screenshot ? "/screenshot" : "/display/select",
                body, TimeSpan.FromSeconds(screenshot ? 15 : 4),
                response => screenshot
                    ? (IsBool(response, "Saved") || IsBool(response, "saved")) &&
                      (response.TryGetProperty("ErrorCode", out var ec)
                        ? ec.ValueKind == JsonValueKind.String : true)
                    : IsBool(response, "saved"));
            if (!result.Success) return (RelayErrorStatus(result), new
            {
                saved = false,
                availability = "Unavailable",
                code = result.Code,
                outcomeUnknown = result.Code == "timeout"
            });
            var value = result.Data!.Value;
            bool saved = Bool(value, screenshot && IsBool(value, "Saved") ? "Saved" : "saved");
            string error = screenshot
                ? Text(value, "ErrorCode") : Text(value, "error");
            string file = screenshot ? Text(value, "File") : "";
            return (200, new
            {
                saved,
                error = saved ? "" : (error.Length == 0 ? "worker_rejected" : error),
                file = saved ? file : "",
                appliesOnEngineRebuild = screenshot ? (bool?)null :
                    (bool?)Bool(value, "appliesOnEngineRebuild"),
                effectiveNow = screenshot ? (bool?)null :
                    (IsBool(value, "effectiveNow") ? (bool?)Bool(value, "effectiveNow") : null),
                selected = screenshot ? (JsonElement?)null :
                    (Field(value, "selected").ValueKind == JsonValueKind.Object
                        ? (JsonElement?)Field(value, "selected") : null),
                source = "NvCapture"
            });
        }
        finally { Volatile.Write(ref _commandInFlight, 0); }
    }

    private static object Failure(Probe probe) => new
    {
        code = probe.Code, message = probe.Message, upstreamStatus = probe.UpstreamStatus
    };


    // Diagnostics only. Worker warmup is not an authorization bypass:
    // health errors, ready=false, and unknown recording state remain unchanged.
    private string StartupPhase(bool workerReady)
    {
        if (!_enabled) return "Disabled";
        if (workerReady)
        {
            Volatile.Write(ref _hasObservedReadyWorker, 1);
            return "Ready";
        }
        if (Volatile.Read(ref _hasObservedReadyWorker) != 0) return "Degraded";
        return _elapsedSinceStartup() < TimeSpan.FromSeconds(30)
            ? "Starting" : "Unavailable";
    }

    private static object RouteProbe(Probe probe, int? version) => new
    {
        routeWired = true,
        schemaVerified = probe.Success,
        schemaVersion = probe.Success ? version : null,
        availability = probe.Success ? "Available" : "Unavailable",
        code = probe.Success ? "" : probe.Code,
        upstreamStatus = probe.UpstreamStatus
    };

    // A read-only contract negotiation. It probes only the real upstream GET
    // endpoints. Neither this method nor a successful /displays response can
    // certify that a write command exists or is safe to invoke.
    internal object Contract()
    {
        var health = Read("health");
        var state = Read("state");
        var engines = Read("engines");
        var displays = Read("displays");
        bool connected = health.Success && state.Success;
        bool ready = connected && Bool(health.Data!.Value, "ready") &&
            Bool(state.Data!.Value, "ready");
        var phase = StartupPhase(ready);
        return new
        {
            schemaVersion = 1,
            source = "NvCapture",
            runtimeEnabled = _enabled,
            availability = connected ? "Available" : "Unavailable",
            workerReady = ready,
            startupPhase = phase,
            startupGraceSeconds = 30,
            workerApiVersion = engines.Success ? (int?)2 : null,
            displayApiVersion = displays.Success ? (int?)1 : null,
            stateContract = state.Success ?
                (IsString(state.Data!.Value, "actualSessionBackend") ? "current" : "legacy")
                : "unavailable",
            recording = state.Success ? (bool?)Bool(state.Data!.Value, "recording") : null,
            endpoints = new
            {
                health = RouteProbe(health, null),
                state = RouteProbe(state, null),
                engines = RouteProbe(engines, 2),
                displays = RouteProbe(displays, 1)
            },
            commands = new
            {
                record = new {
                    routeWired = true, workerCommandVerified = false,
                    gatewayEnabled = _allowRecordCommands && !_testMode,
                    note = "No read-only worker command declaration; POST is not probed."
                },
                displaySelect = new {
                    routeWired = true, workerCommandVerified = false,
                    displayInventoryVerified = displays.Success,
                    gatewayEnabled = _allowDisplayCommands && !_testMode,
                    note = "GET /displays does not verify POST /display/select."
                },
                screenshot = new {
                    routeWired = true, workerCommandVerified = false,
                    displayInventoryVerified = displays.Success,
                    gatewayEnabled = _allowScreenshotCommands && !_testMode,
                    runtimeReady = false,
                    note = "Screenshot POST is never probed without explicit user action."
                },
                replay = new {
                    routeWired = false, workerCommandVerified = false,
                    gatewayEnabled = false
                }
            }
        };
    }

    internal object Engines(string policyPath)
    {
        var health = Read("health");
        var engines = Read("engines");
        return CaptureEngineCatalog.Snapshot(policyPath, _enabled,
            health.Success && Bool(health.Data!.Value, "ready"), engines.Data,
            engines.Success ? "" : engines.Code);
    }

    internal object Capabilities(string policyPath)
    {
        var catalog = JsonSerializer.SerializeToElement(Engines(policyPath));
        return new
        {
            schemaVersion = 1,
            source = "NvCapture",
            availability = catalog.GetProperty("workerTruthAvailable").GetBoolean()
                ? "Available" : "Unavailable",
            workerError = catalog.GetProperty("workerError"),
            policyValid = catalog.GetProperty("policyValid"),
            providers = catalog.GetProperty("providers"),
            workflows = catalog.GetProperty("workflows")
        };
    }

    internal object Status()
    {
        var health = Read("health");
        var state = Read("state");
        var engines = Read("engines");
        bool connected = health.Success && state.Success;
        bool workerReady = connected && Bool(health.Data!.Value, "ready") &&
            Bool(state.Data!.Value, "ready");
        var startupPhase = StartupPhase(workerReady);
        // Worker process readiness != capture engine readiness. The old
        // /state endpoint can prove idle/recording, but not a usable codec.
        bool engineReady = false;
        if (workerReady && engines.Success)
        {
            var capability = engines.Data!.Value;
            var configured = Field(capability, "configured");
            var ffmpeg = Field(capability, "ffmpegBinary");
            var selected = Text(configured, "backend");
            engineReady = Bool(configured, "allowed") &&
                (selected == "Duluka" &&
                    Bool(capability, "nativeRuntimeReady") &&
                    Text(capability, "nativeRuntimeCodec").Length > 0 ||
                 selected == "FFmpeg" &&
                    Bool(ffmpeg, "hardwareEncoderVerified") &&
                    Bool(ffmpeg, "binaryRunnable") &&
                    ArrayOfStrings(ffmpeg, "captureApis") &&
                    ArrayOfStrings(ffmpeg, "encoders") &&
                    Field(ffmpeg, "captureApis").GetArrayLength() > 0 &&
                    Field(ffmpeg, "encoders").GetArrayLength() > 0);
        }
        if (state.Success)
        {
            if (Bool(state.Data!.Value, "recording"))
                Interlocked.Exchange(ref _startOutcomeUnresolved, 0);
            else
                Interlocked.Exchange(ref _stopOutcomeUnresolved, 0);
        }
        return new
        {
            schemaVersion = 1,
            source = "NvCapture",
            runtimeEnabled = _enabled,
            startOutcomeUnresolved = Volatile.Read(ref _startOutcomeUnresolved) != 0,
            stopOutcomeUnresolved = Volatile.Read(ref _stopOutcomeUnresolved) != 0,
            availability = connected ? "Available" : "Unavailable",
            workerReady,
            startupPhase,
            startupGraceSeconds = 30,
            engineEvidenceAvailable = engines.Success,
            workerRuntime = engines.Success ? engines.Data : null,
            runtimeReady = workerReady && (engineReady ||
                (Bool(state.Data!.Value, "recording") &&
                 (Text(state.Data!.Value, "actualSessionBackend") is "Duluka" or "FFmpeg"))),
            recording = state.Success ? (bool?)Bool(state.Data!.Value, "recording") : null,
            stateContract = state.Success ?
                (IsString(state.Data!.Value, "actualSessionBackend") ? "current" : "legacy") : "unavailable",
            actualSessionBackend = state.Success && IsString(state.Data!.Value, "actualSessionBackend") ?
                (string?)Text(state.Data!.Value, "actualSessionBackend") : null,
            actualRunningEngine = state.Success ? Text(state.Data!.Value, "actualRunningEngine") : "",
            lifecycleState = state.Success ? Text(state.Data!.Value, "lifecycleState") : "Unknown",
            busy = state.Success ? (bool?)Bool(state.Data!.Value,
                IsBool(state.Data!.Value, "busy") ? "busy" : "recording") : null,
            isCapturing = state.Success && IsBool(state.Data!.Value, "isCapturing")
                ? (bool?)Bool(state.Data!.Value, "isCapturing") : null,
            replay = new { availability = "Unavailable", commandSupported = false },
            screenshot = new { availability = "Unavailable", commandSupported = false },
            health = health.Data,
            state = state.Data,
            healthError = health.Success ? null : Failure(health),
            stateError = state.Success ? null : Failure(state)
        };
    }

    // Default-deny. No commands are sent unless the user explicitly opts in
    // via config AND supplies explicit confirmation. Only JSON {} is sent upstream.
    internal (int Status, object Body) RecordCommand(bool start, JsonElement request)
    {
        if (!_enabled) return (503, new { code = "runtime_disabled" });
        if (_testMode) return (409, new { code = "test_mode_active",
            message = "Recording commands are blocked while Container test mode is active." });
        if (!_allowRecordCommands) return (403, new { code = "record_gateway_disabled",
            message = "Enable captureRuntime.allowRecordCommands explicitly before forwarding recording commands." });
        if (request.ValueKind != JsonValueKind.Object ||
            !Bool(request, "confirmed") ||
            request.EnumerateObject().Count() != 1)
            return (400, new { code = "confirmation_required",
                message = "Only {\"confirmed\":true} is permitted." });
        if (start && Volatile.Read(ref _startOutcomeUnresolved) != 0)
            return (409, new { code = "start_outcome_unresolved",
                message = "Inspect /v1/capture/status before another start; a previous operation might still complete." });
        if (!start && Volatile.Read(ref _stopOutcomeUnresolved) != 0)
            return (409, new { code = "stop_outcome_unresolved",
                message = "Inspect /v1/capture/status before another stop; the prior finalization may still complete." });
        if (Interlocked.CompareExchange(ref _commandInFlight, 1, 0) != 0)
            return (409, new { code = "capture_command_in_flight" });
        try
        {
            var state = Read("state");
            if (!state.Success)
                return (503, new { code = "capture_state_unavailable", reason = state.Code });
            var s = state.Data!.Value;
            string backend;
            if (start)
            {
                if (Bool(s, "recording")) return (409, new { code = "already_recording" });
                var health = Read("health");
                var engines = Read("engines");
                if (!health.Success || !engines.Success)
                    return (503, new { code = "capture_evidence_unavailable",
                        health = health.Code, engines = engines.Code });
                var e = engines.Data!.Value;
                var configured = Field(e, "configured");
                backend = Text(configured, "backend");
                if (!Bool(configured, "allowed") || backend is not ("Duluka" or "FFmpeg"))
                    return (409, new { code = "unsupported_capture_route" });
                if (backend == "Duluka" &&
                    (!Bool(health.Data!.Value, "ready") || !Bool(s, "ready") ||
                     !Bool(e, "nativeRuntimeReady") || Text(e, "nativeRuntimeCodec").Length == 0))
                    return (503, new { code = "capture_runtime_not_ready" });
                if (backend == "FFmpeg" &&
                    (!Bool(Field(e, "ffmpegBinary"), "binaryRunnable") ||
                     !ArrayOfStrings(Field(e, "ffmpegBinary"), "captureApis") ||
                     !ArrayOfStrings(Field(e, "ffmpegBinary"), "encoders") ||
                     Field(Field(e, "ffmpegBinary"), "captureApis").GetArrayLength() == 0 ||
                     Field(Field(e, "ffmpegBinary"), "encoders").GetArrayLength() == 0))
                    return (503, new { code = "ffmpeg_capability_unavailable" });
                // Do not claim GPU hardware verification from FFmpeg binary introspection.
                // NvCapture validates driver/session actuals when receiving the command.
            }
            else
            {
                // Stopping an active session must remain possible even if the
                // native health flag falls or the /engines endpoint is missing.
                if (!Bool(s, "recording")) return (409, new { code = "not_recording" });
                backend = Text(s, "actualSessionBackend");
                if (backend.Length == 0)
                    backend = Text(Field(s, "runtimeTruth"), "lifecycleOwner");
                if (backend is not ("Duluka" or "FFmpeg"))
                    return (409, new { code = "unsupported_capture_route" });
            }
            var action = start ? "start" : "stop";
            var result = Exchange(HttpMethod.Post, "/record/" + action, "{}",
                TimeSpan.FromSeconds(start ? 19 : 35),
                data => IsBool(data, start ? "started" : "stopped"));
            if (!result.Success)
            {
                if (result.Code == "timeout")
                {
                    if (start) Interlocked.Exchange(ref _startOutcomeUnresolved, 1);
                    else Interlocked.Exchange(ref _stopOutcomeUnresolved, 1);
                }
                return (result.Code == "timeout" ? 504 :
                    result.Code == "worker_authorization_unavailable" ? 503 : 502, new
                {
                    code = result.Code,
                    message = result.Message,
                    outcomeUnknown = result.Code == "timeout",
                    retryRequiresFreshState = true
                });
            }
            var worker = result.Data!.Value;
            bool succeeded = Bool(worker, start ? "started" : "stopped");
            string error = Text(worker, "error");
            bool pending = error is "start_status_pending_timeout" or "stop_status_pending_timeout";
            if (pending)
            {
                if (start) Interlocked.Exchange(ref _startOutcomeUnresolved, 1);
                else Interlocked.Exchange(ref _stopOutcomeUnresolved, 1);
                return (504, new { code = error, outcomeUnknown = true,
                    retryRequiresFreshState = true, workerResult = result.Data });
            }
            return (succeeded ? 200 : 409, new
            {
                accepted = succeeded, action,
                outcomeUnknown = false,
                saved = start ? (bool?)null :
                    (IsBool(worker, "saved") ? (bool?)Bool(worker, "saved") : null),
                finalizationState = start ? "" : Text(worker, "finalizationState"),
                mediaVerified = start ? (bool?)null :
                    (IsBool(worker, "mediaVerified") ? (bool?)Bool(worker, "mediaVerified") : null),
                workerResult = result.Data,
                error = succeeded && (start || Bool(worker, "saved") || !IsBool(worker, "saved")) ? "" : error
            });
        }
        finally { Volatile.Write(ref _commandInFlight, 0); }
    }
}
