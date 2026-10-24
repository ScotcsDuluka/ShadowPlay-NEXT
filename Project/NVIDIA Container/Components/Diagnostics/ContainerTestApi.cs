using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Tasks;

internal sealed class ContainerTestApi
{
    private readonly bool _enabled;
    private readonly TcpListener _listener;
    private readonly Action<string> _log;
    private readonly Action<JsonElement> _publishEvents;
    private readonly ObsRuntime _obsRuntime;
    private readonly Func<int, long, bool> _markNvGraphicsReady;
    private readonly Func<object> _getNvGraphicsStatus;
    private readonly Func<bool> _openNvGraphics;
    private readonly ApplicationOntologyClient _applicationOntologyClient;
    private readonly GameWatcher _gameWatcher;
    private readonly OscAdapterLifecycle _oscAdapterLifecycle;
    private readonly Func<object> _getCaptureEngineCatalog;
    private readonly Func<object> _getCaptureWorkerStatus;
    private readonly Func<object> _getCaptureCapabilities;
    private readonly Func<object> _getCaptureContract;
    private readonly Func<bool, JsonElement, (int Status, object Response)> _forwardRecordCommand;
    private readonly Func<(int Status, object Response)> _getCaptureDisplays;
    private readonly Func<bool, JsonElement, (int Status, object Response)> _forwardDisplayCommand;
    private readonly object _stateLock = new object();
    private bool _recordEnabled;
    private bool _irEnabled;
    private bool _broadcastEnabled;
    private bool _broadcastPaused;
    private bool _modsEnabled = true;
    private int _lastReadyPidLogged;
    private int _lastRejectedReadyPidLogged;

    internal ContainerTestApi(bool enabled, int port, Action<string> log,
        Action<JsonElement> publishEvents, ObsRuntime obsRuntime,
        Func<int, long, bool> markNvGraphicsReady, Func<object> getNvGraphicsStatus,
        Func<bool> openNvGraphics, ApplicationOntologyClient applicationOntologyClient,
        GameWatcher gameWatcher, OscAdapterLifecycle oscAdapterLifecycle,
        Func<object> getCaptureEngineCatalog, Func<object> getCaptureWorkerStatus,
        Func<object> getCaptureCapabilities,
        Func<bool, JsonElement, (int Status, object Response)> forwardRecordCommand,
        Func<(int Status, object Response)> getCaptureDisplays,
        Func<bool, JsonElement, (int Status, object Response)> forwardDisplayCommand,
        Func<object> getCaptureContract)
    {
        _enabled = enabled;
        _log = log;
        _publishEvents = publishEvents;
        _obsRuntime = obsRuntime;
        _markNvGraphicsReady = markNvGraphicsReady;
        _getNvGraphicsStatus = getNvGraphicsStatus;
        _openNvGraphics = openNvGraphics;
        _applicationOntologyClient = applicationOntologyClient;
        _gameWatcher = gameWatcher;
        _oscAdapterLifecycle = oscAdapterLifecycle;
        _getCaptureEngineCatalog = getCaptureEngineCatalog;
        _getCaptureWorkerStatus = getCaptureWorkerStatus;
        _getCaptureCapabilities = getCaptureCapabilities;
        _getCaptureContract = getCaptureContract;
        _forwardRecordCommand = forwardRecordCommand;
        _getCaptureDisplays = getCaptureDisplays;
        _forwardDisplayCommand = forwardDisplayCommand;
        _listener = new TcpListener(IPAddress.Loopback, port);
    }

    internal void Start()
    {
        try
        {
            _listener.Start();
            _log("[test-api] listening on 127.0.0.1:" + ((IPEndPoint)_listener.LocalEndpoint).Port);
            _ = AcceptRequests();
        }
        catch (Exception ex)
        {
            _log("[test-api] failed to start: " + ex.Message);
        }
    }

    private async Task AcceptRequests()
    {
        while (true)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
            }
            catch (SocketException)
            {
                return;
            }

            _ = Task.Run(() => HandleRequest(client));
        }
    }

    private void HandleRequest(TcpClient client)
    {
        using (client)
        {
            try
            {
                // A loopback client that connects but never sends headers must not pin a
                // worker thread forever; these are synchronous Stream reads/writes.
                client.ReceiveTimeout = 3000;
                client.SendTimeout = 3000;
                client.NoDelay = true;

                var remote = client.Client.RemoteEndPoint as IPEndPoint;
                var stream = client.GetStream();
                if (remote == null || !IPAddress.IsLoopback(remote.Address))
                {
                    Write(stream, 403, new { error = "loopback only" });
                    return;
                }

                var request = ReadRequest(stream);
                var security = ControlRequestSecurity.Validate(request.Method, request.Headers);
                if (security.Status != 200)
                {
                    Write(stream, security.Status, new { error = security.Error });
                    return;
                }
                if (request.Method == "GET" && request.Path == "/v1/ontology/status")
                {
                    var result = _applicationOntologyClient.GetStatus();
                    Write(stream, result.StatusCode, result.Body);
                    return;
                }

                if (request.Method == "GET" && request.Path == "/v1/game-watcher/status")
                {
                    Write(stream, 200, _gameWatcher.GetStatus());
                    return;
                }

                if (request.Method == "GET" &&
                    request.Path is "/v1/osc-adapter/status" or "/v1/hook-overlay/status")
                {
                    Write(stream, 200, _oscAdapterLifecycle.GetStatus());
                    return;
                }

                if (request.Method == "POST" && request.Path == "/v1/ontology/resolve")
                {
                    if (request.Body.Length > 16 * 1024)
                    {
                        Write(stream, 413, new
                        {
                            error = new { code = "invalid_request", message = "Request body exceeds 16 KiB." }
                        });
                        return;
                    }

                    using var ontologyDocument = JsonDocument.Parse(request.Body);
                    var result = _applicationOntologyClient.Resolve(ontologyDocument.RootElement);
                    Write(stream, result.StatusCode, result.Body);
                    return;
                }

                if (request.Method == "GET" && request.Path == "/v1/status")
                {
                    var obs = _obsRuntime?.GetStatus();
                    Write(stream, 200, new
                    {
                        ok = true,
                        testMode = _enabled,
                        obsEnabled = _obsRuntime?.Enabled ?? false,
                        obs
                    });
                    return;
                }

                if (request.Method == "GET" && request.Path == "/v1/capture/engines")
                {
                    Write(stream, 200, _getCaptureEngineCatalog());
                    return;
                }
                if (request.Method == "GET" && request.Path == "/v1/capture/status")
                {
                    Write(stream, 200, _getCaptureWorkerStatus());
                    return;
                }
                if (request.Method == "GET" && request.Path == "/v1/capture/contract")
                {
                    Write(stream, 200, _getCaptureContract());
                    return;
                }
                if (request.Method == "GET" && request.Path == "/v1/capture/capabilities")
                {
                    Write(stream, 200, _getCaptureCapabilities());
                    return;
                }
                if (request.Method == "POST" && request.Path is
                    "/v1/capture/record/start" or "/v1/capture/record/stop")
                {
                    using var commandDocument = JsonDocument.Parse(request.Body);
                    var result = _forwardRecordCommand(
                        request.Path == "/v1/capture/record/start", commandDocument.RootElement);
                    Write(stream, result.Status, result.Response);
                    return;
                }
                if (request.Method == "GET" && request.Path == "/v1/capture/displays")
                {
                    var result = _getCaptureDisplays();
                    Write(stream, result.Status, result.Response);
                    return;
                }
                if (request.Method == "POST" && request.Path is
                    "/v1/capture/display/select" or "/v1/capture/screenshot")
                {
                    if (request.Body.Length > 4096)
                    {
                        Write(stream, 413, new { saved = false, code = "capture_request_too_large" });
                        return;
                    }
                    using var displayDocument = JsonDocument.Parse(request.Body);
                    var result = _forwardDisplayCommand(
                        request.Path == "/v1/capture/screenshot", displayDocument.RootElement);
                    Write(stream, result.Status, result.Response);
                    return;
                }
                if (request.Path.StartsWith("/v1/capture/replay/", StringComparison.Ordinal))
                {
                    Write(stream, 501, new { code = "not_supported_by_worker_contract",
                        availability = "Unavailable" });
                    return;
                }

                if (request.Method == "GET" && request.Path == "/v1/gpu/graphics/status")
                {
                    Write(stream, 200, _getNvGraphicsStatus());
                    return;
                }
                if (request.Method == "POST" && request.Path == "/v1/gpu/graphics/open")
                {
                    if (!_openNvGraphics())
                    {
                        Write(stream, 503, new { error = "NvGraphics is not ready or its window is unavailable." });
                        return;
                    }
                    Write(stream, 200, new { opened = true });
                    return;
                }
                if (request.Method == "POST" && request.Path == "/v1/gpu/graphics/ready")
                {
                    using var readyDocument = JsonDocument.Parse(request.Body);
                    var pid = readyDocument.RootElement.TryGetProperty("pid", out var pidValue) &&
                        pidValue.TryGetInt32(out var processId) ? processId : 0;
                    var windowHandle = readyDocument.RootElement.TryGetProperty("windowHandle", out var handleValue) &&
                        handleValue.TryGetInt64(out var handle) ? handle : 0;
                    if (!_markNvGraphicsReady(pid, windowHandle))
                    {
                        lock (_stateLock)
                        {
                            if (_lastRejectedReadyPidLogged != pid)
                            {
                                _lastRejectedReadyPidLogged = pid;
                                _log("[NvGraphics] readiness rejected (PID " + pid + ", HWND " + windowHandle + ")");
                            }
                        }
                        Write(stream, 409, new { error = "NvGraphics process identity could not be verified." });
                        return;
                    }
                    lock (_stateLock)
                    {
                        if (_lastReadyPidLogged != pid)
                        {
                            _lastReadyPidLogged = pid;
                            _log("[NvGraphics] readiness accepted (PID " + pid + ")");
                        }
                    }
                    Write(stream, 200, new { ready = true, pid });
                    return;
                }

                if (request.Method == "POST" && request.Path.StartsWith("/v1/hotkey/", StringComparison.Ordinal))
                {
                    var name = request.Path.Substring("/v1/hotkey/".Length);
                    var hotkeyId = HotkeyId(name);
                    if (hotkeyId == null)
                    {
                        Write(stream, 404, new { error = "unknown hotkey" });
                        return;
                    }

                    var events = new[]
                    {
                        new { name = "/ShadowPlay/v.1.0/Hotkey", data = new { hotkeyId, hotKeyName = name } }
                    };
                    using var eventsDocument = JsonDocument.Parse(JsonSerializer.Serialize(events));
                    _publishEvents?.Invoke(eventsDocument.RootElement.Clone());
                    _log("[hotkey] " + name + " -> OSC event " + hotkeyId);
                    Write(stream, 200, new { handled = true, hotkeyId, testMode = _enabled });
                    return;
                }

                // NvGraphics GPU-state bridge: validated receive + fixed-name forward.
                if (request.Method == "POST" && request.Path == "/v1/gpu/runtime")
                {
                    var (status, response) = GpuRuntimeRoute.Handle(
                        request.Body, _publishEvents, _log);
                    Write(stream, status, response);
                    return;
                }
                if (request.Method == "POST" && request.Path == "/v1/gpu/preference")
                {
                    var (status, response) = GpuRuntimeRoute.HandlePreference(
                        request.Body, _publishEvents, _log);
                    Write(stream, status, response);
                    return;
                }
                if (request.Method == "POST" && request.Path == "/v1/gpu/inventory")
                {
                    var (status, response) = GpuRuntimeRoute.HandleInventory(
                        request.Body, _publishEvents, _log);
                    Write(stream, status, response);
                    return;
                }
                if (request.Method == "GET" && request.Path == "/v1/gpu/profiles")
                {
                    var (status, response) = GpuProfilesRoute.GetProfiles();
                    Write(stream, status, response);
                    return;
                }
                if (request.Method == "GET" && request.Path == "/v1/gpu/running-games")
                {
                    var (status, response) = GpuProfilesRoute.GetRunningGames();
                    Write(stream, status, response);
                    return;
                }
                if (request.Method == "POST" && request.Path == "/v1/gpu/profiles/add")
                {
                    var (status, response) = GpuProfilesRoute.AddRunningGame(request.Body);
                    Write(stream, status, response);
                    return;
                }
                if (request.Method == "GET" && request.Path == "/v1/gpu/manager")
                {
                    var (status, response) = GpuManagerRoute.GetState();
                    Write(stream, status, response);
                    return;
                }
                if (request.Method == "GET" && request.Path == "/v1/gpu/file-picker/executable")
                {
                    var (status, response) = GpuFilePickerRoute.BrowseExecutable();
                    Write(stream, status, response);
                    return;
                }
                if (request.Method == "GET" && request.Path == "/v1/gpu/running-profile-paths")
                {
                    var (status, response) = GpuManagerRoute.GetRunningProfilePaths();
                    Write(stream, status, response);
                    return;
                }
                if (request.Method == "POST" && request.Path == "/v1/gpu/restart")
                {
                    var (status, response) = GpuProfilesRoute.RestartRunningGame(request.Body);
                    Write(stream, status, response);
                    return;
                }
                if (request.Method == "POST" && request.Path == "/v1/gpu/profiles/save")
                {
                    var (status, response) = GpuManagerRoute.SaveProfile(request.Body);
                    Write(stream, status, response);
                    return;
                }
                if (request.Method == "POST" && request.Path == "/v1/gpu/profiles/remove")
                {
                    var (status, response) = GpuManagerRoute.RemoveProfile(
                        request.Body, _publishEvents);
                    Write(stream, status, response);
                    return;
                }
                if (request.Method == "POST" && request.Path == "/v1/gpu/preference/apply")
                {
                    var (status, response) = GpuManagerRoute.ApplyPreference(
                        request.Body, _publishEvents);
                    Write(stream, status, response);
                    return;
                }
                if (request.Method == "POST" && request.Path == "/v1/gpu/preference/remove")
                {
                    var (status, response) = GpuManagerRoute.RemoveWindowsEntry(
                        request.Body, _publishEvents);
                    Write(stream, status, response);
                    return;
                }
                if (request.Method == "POST" && request.Path == "/v1/gpu/backups/restore")
                {
                    var (status, response) = GpuManagerRoute.RestoreBackup(request.Body);
                    Write(stream, status, response);
                    return;
                }
                if (request.Method == "POST" && request.Path == "/v1/gpu/runtime/check")
                {
                    var (status, response) = GpuManagerRoute.CheckRuntime(
                        request.Body, _publishEvents);
                    Write(stream, status, response);
                    return;
                }
                var dispatchRequest = request.Method == "POST" && request.Path == "/v1/dispatch";
                var directObsRequest = request.Path is "/v1/obs/settings" or "/v1/obs/status" or "/v1/obs/test";
                if (!dispatchRequest && !directObsRequest)
                {
                    Write(stream, 404, new { error = "not found" });
                    return;
                }

                using var document = JsonDocument.Parse(request.Body.Length == 0
                    ? "{}" : System.Text.Encoding.UTF8.GetString(request.Body));
                var root = document.RootElement;
                var method = dispatchRequest ? ReadString(root, "method") : request.Method;
                var path = dispatchRequest ? ReadString(root, "path") : request.Path;
                var body = dispatchRequest
                    ? (root.TryGetProperty("body", out var value) ? value : default)
                    : root;
                Write(stream, 200, Dispatch(method, path, body));
            }
            catch (JsonException ex)
            {
                try { Write(client.GetStream(), 400, new { error = "invalid JSON", message = ex.Message }); } catch { }
            }
            catch (IOException)
            {
                try { Write(client.GetStream(), 400, new { error = "malformed HTTP request" }); } catch { }
            }
            catch (Exception ex)
            {
                _log("[test-api] request failed: " + ex.Message);
                try { Write(client.GetStream(), 500, new { error = "test dispatch failed" }); } catch { }
            }
        }

    }

    private static string HotkeyId(string name)
    {
        switch (name.ToLowerInvariant())
        {
            case "openshare":
            case "overlaytoggle": return "OSC";
            case "screenshot": return "Screenshot";
            case "recordtoggle": return "Manual";
            case "recordsave": return "DVR";
            case "dvrtoggle":
            case "instantreplaytoggle": return "DVRToggle";
            case "broadcasttoggle": return "GameCast";
            case "broadcastpausetoggle": return "PauseResume";
            case "cameratoggle": return "Camera";
            case "mictoggle": return "MicToggle";
            case "fps": return "FPS";
            case "nvcameraui": return "NvCameraUI";
            case "modsui": return "ModsUI";
            case "modstoggle": return "ModsToggle";
            case "modspreset1": return "ModsPreset1";
            case "modspreset2": return "ModsPreset2";
            case "modspreset3": return "ModsPreset3";
            case "modspresetcycle": return "ModsPresetCycle";
            case "commentstoggle": return "CommentsToggle";
            case "overlayaswitch": return "CustomOverlayA";
            case "overlaybswitch": return "CustomOverlayB";
            case "overlaycswitch": return "CustomOverlayC";
            case "pmocoverlay": return "PMOCOverlay";
            case "pmocoverlaycycle": return "PMOCOverlayCycle";
            case "pmocresetaveragemetrics": return "PMOCResetAverageMetrics";
            case "pmocloggingtoggle": return "PMOCLoggingToggle";
            default: return null;
        }
    }

    private static (string Method, string Path, byte[] Body, Dictionary<string, string> Headers) ReadRequest(NetworkStream stream)
    {
        var headerBytes = new List<byte>();
        while (headerBytes.Count < 16384)
        {
            var value = stream.ReadByte();
            if (value < 0) throw new IOException("Client closed before sending HTTP headers");
            headerBytes.Add((byte)value);
            var count = headerBytes.Count;
            if (count >= 4 && headerBytes[count - 4] == 13 && headerBytes[count - 3] == 10 &&
                headerBytes[count - 2] == 13 && headerBytes[count - 1] == 10)
                break;
        }
        if (headerBytes.Count >= 16384) throw new IOException("HTTP headers exceed the allowed size");

        var lines = System.Text.Encoding.ASCII.GetString(headerBytes.ToArray())
            .Split(new[] { "\r\n" }, StringSplitOptions.None);
        var requestLine = lines[0].Split(' ');
        if (requestLine.Length < 2) throw new IOException("Invalid HTTP request line");
        var path = new Uri("http://127.0.0.1" + requestLine[1]).AbsolutePath;
        var contentLength = 0;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i < lines.Length; i++)
        {
            var separator = lines[i].IndexOf(':');
            if (separator <= 0) continue;
            var name = lines[i].Substring(0, separator).Trim();
            var value = lines[i].Substring(separator + 1).Trim();
            if (!headers.TryAdd(name, value))
                throw new IOException("Duplicate HTTP request header");
            if (!name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) continue;
            if (!int.TryParse(value, out contentLength) ||
                contentLength < 0 || contentLength > 262144)
                throw new IOException("Invalid or oversized Content-Length");
        }
        if (headers.ContainsKey("Transfer-Encoding"))
            throw new IOException("Chunked requests are not supported by the control API");

        var body = new byte[contentLength];
        var offset = 0;
        while (offset < body.Length)
        {
            var read = stream.Read(body, offset, body.Length - offset);
            if (read == 0) throw new IOException("Client closed before sending the request body");
            offset += read;
        }
        return (requestLine[0], path, body, headers);
    }

    private object Dispatch(string method, string path, JsonElement body)
    {
        if (path == "/v1/obs/settings")
        {
            if (method == "GET") return Reply(_obsRuntime.GetSettings());
            if (method == "POST")
            {
                var error = _obsRuntime.UpdateSettings(body);
                return error == null ? Reply(_obsRuntime.GetSettings()) : ErrorReply(error);
            }
        }
        if (path == "/v1/obs/status" && method == "GET")
            return Reply(_obsRuntime.GetStatus());
        if (path == "/v1/obs/test" && method == "POST")
            return Reply(_obsRuntime.TestConnection());

        if (IsMenuSupportRoute(method, path))
        {
            lock (_stateLock) return DispatchMenuSupport(method, path, body);
        }

        if (!_enabled)
            return new { handled = false };

        if (path.StartsWith("/ShadowPlay/v.1.0/", StringComparison.Ordinal))
            return DispatchShadowPlay(method, path, body);

        if (path.StartsWith("/NvCamera/v.1.", StringComparison.Ordinal))
        {
            lock (_stateLock) return DispatchGameFilter(method, path, body);
        }

        if (path == "/Duluka/v.1.0/State" && method == "GET")
        {
            lock (_stateLock)
            {
                return Reply(new
                {
                    recordState = _recordEnabled ? "Recording" : "Idle",
                    irState = _irEnabled ? "Armed" : "Idle",
                    savePath = "",
                    testMode = true
                });
            }
        }

        if (path == "/Duluka/v.1.0/Actual" && method == "POST")
            return ErrorReply("Capture engine actuals are disabled in test mode", 409);

        return new { handled = false };
    }

    private static bool IsMenuSupportRoute(string method, string path)
    {
        return (method == "GET" &&
                (path == "/NvCamera/v.1.0/Compatible" ||
                 path == "/NvCamera/v.1.0/GetNvCameraConfig" ||
                 path == "/ShadowPlay/v.1.0/Screenshot/Support")) ||
            (method == "POST" &&
                (path == "/NvCamera/v.1.0/GetAvailable" ||
                 path == "/NvCamera/v.1.0/GetFreestyleSupport" ||
                 path == "/NvCamera/v.1.0/EnableMods"));
    }

    private object DispatchMenuSupport(string method, string path, JsonElement body)
    {
        var testMode = _enabled;
        if (method == "GET" && path == "/NvCamera/v.1.0/Compatible")
            return Reply(testMode ? new { compatible = true, testMode = true } : new { compatible = true });
        if (method == "GET" && path == "/ShadowPlay/v.1.0/Screenshot/Support")
            return Reply(testMode ? new { support = true, testMode = true } : new { support = true });
        if (method == "POST" && path == "/NvCamera/v.1.0/GetAvailable")
        {
            // NvCameraService waits for this notification; the REST reply alone times out.
            var features = new[] { "sdk", "mods", "allowOffline" };
            var events = new[]
            {
                Event("/NvCamera/v.1.0/Notifications", new { type = "available", features })
            };
            return Reply(testMode
                ? new { available = true, supported = true, testMode = true }
                : new { available = true, supported = true }, events);
        }
        if (method == "POST" && path == "/NvCamera/v.1.0/GetFreestyleSupport")
            return Reply(testMode ? new { supported = true, testMode = true } : new { supported = true });
        if (method == "GET" && path == "/NvCamera/v.1.0/GetNvCameraConfig")
            return Reply(testMode
                ? new { enabled = _modsEnabled, testMode = true }
                : new { enabled = _modsEnabled });
        if (method == "POST" && path == "/NvCamera/v.1.0/EnableMods")
        {
            if (!TryBoolean(body, "enabled", out var enabled) && !TryBoolean(body, "enable", out enabled))
                return ErrorReply("EnableMods requires an enabled boolean");
            _modsEnabled = enabled;
            return Reply(testMode
                ? new { success = true, enabled = _modsEnabled, testMode = true }
                : new { success = true, enabled = _modsEnabled });
        }

        return new { handled = false };
    }

    private object DispatchShadowPlay(string method, string path, JsonElement body)
    {
        lock (_stateLock)
        {
            if (path == "/ShadowPlay/v.1.0/OSC/MainView" && method == "POST")
                return Reply(new
                {
                    instantReplayEnabled = _irEnabled,
                    instantReplayRunning = _irEnabled,
                    manualRecordEnabled = _recordEnabled,
                    broadcastProvider = "AlwaysAsk",
                    webcamPresent = false,
                    webcamShown = false,
                    micPresentCount = 0,
                    micMode = "off",
                    audioMode = "both",
                    coplayEnabled = false,
                    testMode = true
                });

            if (path == "/ShadowPlay/v.1.0/Capture/State" && method == "GET")
            {
                if (_recordEnabled) return Reply(new { captureMode = 0, recordingState = 1, testMode = true });
                if (_irEnabled) return Reply(new { captureMode = 1, recordingState = 1, testMode = true });
                if (_broadcastEnabled) return Reply(new { captureMode = 2, recordingState = 1, testMode = true });
                return Reply(new { captureMode = 0, recordingState = 0, testMode = true });
            }

            if (path == "/ShadowPlay/v.1.0/Record/Enable")
            {
                if (method == "GET") return Reply(new { status = _recordEnabled, testMode = true });
                if (method == "POST" && TryBoolean(body, "status", out var enabled))
                {
                    var changed = _recordEnabled != enabled;
                    _recordEnabled = enabled;
                    return Reply(
                        new { status = enabled, accepted = true, testMode = true },
                        changed ? new object[]
                        {
                            Event("/ShadowPlay/v.1.0/Record/Enable", new { status = enabled, testMode = true }),
                            Event("/ShadowPlay/v.1.0/Record/Running", new { running = enabled, testMode = true })
                        } : Array.Empty<object>());
                }
                if (method == "POST") return ErrorReply("Record enable requires a boolean status field");
            }

            if (path == "/ShadowPlay/v.1.0/Record/Running" && method == "GET")
                return Reply(new { running = _recordEnabled, testMode = true });
            if (path == "/ShadowPlay/v.1.0/Record/Save" && method == "POST")
            {
                var changed = _recordEnabled;
                _recordEnabled = false;
                return Reply(new { status = true, saved = true, testMode = true },
                    changed ? new object[]
                    {
                        Event("/ShadowPlay/v.1.0/Record/Enable", new { status = false, testMode = true }),
                        Event("/ShadowPlay/v.1.0/Record/Running", new { running = false, testMode = true }),
                        Event("/ShadowPlay/v.1.0/Notification", new { notification = "recordingSaved", result = 0, testMode = true })
                    } : Array.Empty<object>());
            }

            if (path == "/ShadowPlay/v.1.0/InstantReplay/Enable")
            {
                if (method == "GET") return Reply(new { status = _irEnabled, testMode = true });
                if (method == "POST" && TryBoolean(body, "status", out var enabled))
                {
                    var changed = _irEnabled != enabled;
                    _irEnabled = enabled;
                    return Reply(new { status = enabled, accepted = true, testMode = true },
                        changed ? new object[]
                        {
                            Event("/ShadowPlay/v.1.0/InstantReplay/Enable", new { status = enabled, testMode = true }),
                            Event("/ShadowPlay/v.1.0/InstantReplay/Started", new { started = enabled, testMode = true })
                        } : Array.Empty<object>());
                }
                if (method == "POST") return ErrorReply("Instant Replay enable requires a boolean status field");
            }

            if (path == "/ShadowPlay/v.1.0/InstantReplay/Running" && method == "GET")
                return Reply(new { running = _irEnabled, testMode = true });
            if (path == "/ShadowPlay/v.1.0/InstantReplay/Save" && method == "POST")
                return Reply(new { status = true, saved = true, testMode = true },
                    new[] { Event("/ShadowPlay/v.1.0/InstantReplay/Save", new { status = true, testMode = true }) });

            if (path == "/ShadowPlay/v.1.0/Broadcast/Enable")
            {
                if (method == "GET") return Reply(new { status = _broadcastEnabled, testMode = true });
                if (method == "POST" && TryBoolean(body, "status", out var enabled))
                {
                    var changed = _broadcastEnabled != enabled;
                    _broadcastEnabled = enabled;
                    return Reply(new { status = enabled, accepted = true, testMode = true },
                        changed ? new object[]
                        {
                            Event("/ShadowPlay/v.1.0/Broadcast/Enable", new { status = enabled, testMode = true })
                        } : Array.Empty<object>());
                }
                if (method == "POST") return ErrorReply("Broadcast enable requires a boolean status field");
            }

            if (path == "/ShadowPlay/v.1.0/Broadcast/Running" && method == "GET")
                return Reply(new { running = _broadcastEnabled, testMode = true });
            if (path == "/ShadowPlay/v.1.0/Broadcast/Pause" && method == "POST" &&
                TryBoolean(body, "status", out var paused))
            {
                var changed = _broadcastPaused != paused;
                _broadcastPaused = paused;
                return Reply(new { status = true, paused = _broadcastPaused, testMode = true },
                    changed ? new[] { Event("/ShadowPlay/v.1.0/Broadcast/Pause", new { status = paused, testMode = true }) }
                        : Array.Empty<object>());
            }
            if (path == "/ShadowPlay/v.1.0/Broadcast/Pause" && method == "POST")
                return ErrorReply("Broadcast pause requires a boolean status field");

            if (path == "/ShadowPlay/v.1.0/Screenshot/Support" && method == "GET")
                return Reply(new { support = true, testMode = true });
            if (path == "/ShadowPlay/v.1.0/Screenshot/Capture" && method == "POST")
                return Reply(new { success = true, captured = true, testMode = true },
                    new[] { Event("/ShadowPlay/v.1.0/Notification", new { notification = "screenshot", result = 0, testMode = true }) });

            return new { handled = false };
        }
    }

    private object DispatchGameFilter(string method, string path, JsonElement body)
    {
        if (method == "GET" && path.EndsWith("/Compatible", StringComparison.Ordinal))
            return Reply(new { compatible = true, testMode = true });
        if (path.EndsWith("/GetAvailable", StringComparison.Ordinal))
            return Reply(new { available = true, supported = true, testMode = true });
        if (method == "POST" && path.EndsWith("/Capture/Control", StringComparison.Ordinal))
        {
            if (!TryBoolean(body, "capture", out var capture))
                return ErrorReply("Capture control requires a capture boolean");

            return Reply(
                new { success = true, capture, testMode = true },
                new[]
                {
                    // The notification's capture field carries NVCAMERA_STATUS, not the requested boolean.
                    Event("/NvCamera/v.1.0/Notifications", new { type = "captureControlChange", capture = 1 })
                });
        }
        if (path.EndsWith("/GetFreestyleSupport", StringComparison.Ordinal))
            return Reply(new { supported = true, testMode = true });
        if (path.EndsWith("/GetFilters", StringComparison.Ordinal))
            return Reply(new { filters = Array.Empty<object>(), supported = true, testMode = true });
        if (path.EndsWith("/GetIntegration", StringComparison.Ordinal))
            return Reply(new { integrated = true, testMode = true });
        if (path.EndsWith("/GetNvCameraConfig", StringComparison.Ordinal))
            return Reply(new { enabled = _modsEnabled, testMode = true });
        if (path.EndsWith("/SetFilterAndAttributesSupported", StringComparison.Ordinal) ||
            path.EndsWith("/SetMultipleFilterAPISupport", StringComparison.Ordinal) ||
            path.EndsWith("/ReshadeSupported", StringComparison.Ordinal))
            return Reply(new { supported = true, testMode = true });
        if (path.EndsWith("/EnableMods", StringComparison.Ordinal) && method == "POST")
        {
            if (TryBoolean(body, "enabled", out var enabled) || TryBoolean(body, "enable", out enabled))
                _modsEnabled = enabled;
            return Reply(new { success = true, enabled = _modsEnabled, testMode = true });
        }

        return Reply(new { success = true, supported = true, testMode = true });
    }

    private static object Reply(object body, object[] events = null)
    {
        return new { handled = true, statusCode = 200, body, events = events ?? Array.Empty<object>() };
    }

    private static object ErrorReply(string message, int statusCode = 400)
    {
        return new
        {
            handled = true,
            statusCode,
            body = new { type = "Error", code = 4, codeText = "ET_INVALID_DATA", message },
            events = Array.Empty<object>()
        };
    }

    private static object Event(string name, object data) => new { name, data };

    private static string ReadString(JsonElement element, string property)
    {
        return element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : "";
    }

    private static bool TryBoolean(JsonElement element, string property, out bool value)
    {
        value = false;
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(property, out var field) ||
            (field.ValueKind != JsonValueKind.True && field.ValueKind != JsonValueKind.False))
            return false;
        value = field.GetBoolean();
        return true;
    }

    private static void Write(NetworkStream stream, int statusCode, object value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        var statusText = statusCode == 200 ? "OK"
            : statusCode == 204 ? "No Content"
            : statusCode == 400 ? "Bad Request"
            : statusCode == 403 ? "Forbidden"
            : statusCode == 404 ? "Not Found"
            : statusCode == 409 ? "Conflict"
            : statusCode == 413 ? "Payload Too Large"
            : statusCode == 415 ? "Unsupported Media Type"
            : statusCode == 502 ? "Bad Gateway"
            : statusCode == 503 ? "Service Unavailable"
            : statusCode == 504 ? "Gateway Timeout"
            : statusCode == 501 ? "Not Implemented"
            : "Internal Server Error";
        var headers = System.Text.Encoding.ASCII.GetBytes(
            "HTTP/1.1 " + statusCode + " " + statusText + "\r\n" +
            "Content-Type: application/json; charset=utf-8\r\n" +
            "Content-Length: " + bytes.Length + "\r\nConnection: close\r\n\r\n");
        stream.Write(headers, 0, headers.Length);
        stream.Write(bytes, 0, bytes.Length);
    }
}
