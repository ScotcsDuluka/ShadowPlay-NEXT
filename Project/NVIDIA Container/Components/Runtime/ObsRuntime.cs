using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

internal sealed class ObsRuntime : IDisposable
{
    [ComImport]
    [Guid("70AE66F2-C809-4E4F-8915-BDCB406B7993")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMediaFoundationSourceReader
    {
        [PreserveSig] int GetStreamSelection(uint streamIndex, out int selected);
        [PreserveSig] int SetStreamSelection(uint streamIndex, int selected);
        [PreserveSig] int GetNativeMediaType(uint streamIndex, uint typeIndex, IntPtr mediaType);
        [PreserveSig] int GetCurrentMediaType(uint streamIndex, IntPtr mediaType);
        [PreserveSig] int SetCurrentMediaType(uint streamIndex, IntPtr reserved, IntPtr mediaType);
        [PreserveSig] int SetCurrentPosition(ref Guid timeFormat, ref MediaFoundationPropVariant position);
        [PreserveSig] int ReadSample(uint streamIndex, uint controlFlags, IntPtr actualStreamIndex,
            IntPtr streamFlags, IntPtr timestamp, IntPtr sample);
        [PreserveSig] int Flush(uint streamIndex);
        [PreserveSig] int GetServiceForStream(uint streamIndex, ref Guid service, ref Guid interfaceId,
            out IntPtr result);
        [PreserveSig] int GetPresentationAttribute(uint streamIndex, ref Guid attribute,
            out MediaFoundationPropVariant value);
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct MediaFoundationPropVariant
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public long Int64Value;
    }

    private sealed class ObsSettings
    {
        public bool Enabled { get; set; }
        public string Host { get; set; } = "127.0.0.1";
        public int Port { get; set; } = 4455;
        public string Password { get; set; } = "";
        public bool ForwardRecordStateChanged { get; set; } = true;
    }

    private void SynchronizeStatus(ClientWebSocket socket)
    {
        SynchronizeStatusRequest(socket, "GetRecordStatus", responseData =>
            _recording = ReadBoolean(responseData, "outputActive", _recording));
        lock (_sync)
        {
            if (!ReferenceEquals(_socket, socket)) return;
        }
        _log("[obs] synchronized recording state");
    }

    private void SynchronizeStatusRequest(ClientWebSocket socket, string requestType, Action<JsonElement> apply)
    {
        try
        {
            lock (_sync)
            {
                if (!ReferenceEquals(_socket, socket)) return;
            }
            var response = SendRequestAsync(requestType, null, TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
            if (IsSuccessful(response, out _) && response.TryGetProperty("responseData", out var responseData))
                apply(responseData);
        }
        catch (Exception ex) { _log("[obs] " + requestType + " state unavailable: " + ex.Message); }
    }

    internal bool Enabled
    {
        get { lock (_sync) return _settings.Enabled; }
    }

    private readonly string _configPath;
    private readonly Action<string> _log;
    private readonly Action<string, object> _publishEvent;
    private readonly object _sync = new object();
    private static readonly object MediaFoundationSync = new object();
    private readonly SemaphoreSlim _sendLock = new SemaphoreSlim(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pending =
        new ConcurrentDictionary<string, TaskCompletionSource<JsonElement>>();
    private ObsSettings _settings;
    private ClientWebSocket _socket;
    private CancellationTokenSource _runCancellation;
    private Task _runTask;
    private int _requestId;
    private volatile bool _connected;
    private volatile bool _recording;
    private volatile string _lastError = "";

    internal ObsRuntime(string configPath, Action<string> log, Action<string, object> publishEvent)
    {
        _configPath = configPath;
        _log = log;
        _publishEvent = publishEvent;
        _settings = ReadSettings();
    }

    internal void Start()
    {
        lock (_sync)
        {
            if (_runTask != null) return;
            var cancellation = new CancellationTokenSource();
            _runCancellation = cancellation;
            _runTask = Task.Run(() => RunLoop(cancellation.Token));
        }
        _log("[obs] runtime started; enabled=" + _settings.Enabled + " endpoint=" + _settings.Host + ":" + _settings.Port);
    }

    internal object GetSettings()
    {
        lock (_sync)
        {
            return new
            {
                enabled = _settings.Enabled,
                host = _settings.Host,
                port = _settings.Port,
                passwordConfigured = !string.IsNullOrEmpty(_settings.Password),
                forward = new
                {
                    record_state_changed = _settings.ForwardRecordStateChanged
                }
            };
        }
    }

    internal object GetStatus()
    {
        lock (_sync)
        {
            return new
            {
                enabled = _settings.Enabled,
                connected = _connected,
                recording = _recording,
                host = _settings.Host,
                port = _settings.Port,
                error = _lastError
            };
        }
    }

    internal string UpdateSettings(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object) return "OBS settings must be a JSON object";
        if (!body.TryGetProperty("enabled", out var enabledElement) ||
            (enabledElement.ValueKind != JsonValueKind.True && enabledElement.ValueKind != JsonValueKind.False))
            return "OBS settings require a boolean enabled field";
        if (!body.TryGetProperty("host", out var hostElement) || hostElement.ValueKind != JsonValueKind.String)
            return "OBS settings require a host";
        var host = hostElement.GetString()?.Trim();
        if (string.IsNullOrWhiteSpace(host) || host.Length > 253 ||
            Uri.CheckHostName(host) == UriHostNameType.Unknown)
            return "OBS host is invalid";
        if (!body.TryGetProperty("port", out var portElement) || !portElement.TryGetInt32(out var port) ||
            port < 1 || port > 65535)
            return "OBS port must be an integer from 1 to 65535";

        lock (_sync)
        {
            var forwardRecordStateChanged = _settings.ForwardRecordStateChanged;
            if (body.TryGetProperty("forward", out var forwardElement) &&
                forwardElement.ValueKind == JsonValueKind.Object)
            {
                forwardRecordStateChanged = ReadBoolean(forwardElement, "record_state_changed", forwardRecordStateChanged);
            }

            var updated = new ObsSettings
            {
                Enabled = enabledElement.GetBoolean(),
                Host = host,
                Port = port,
                Password = _settings.Password,
                ForwardRecordStateChanged = forwardRecordStateChanged
            };
            if (body.TryGetProperty("clearPassword", out var clearElement) &&
                clearElement.ValueKind == JsonValueKind.True)
                updated.Password = "";
            else if (body.TryGetProperty("password", out var passwordElement) &&
                     passwordElement.ValueKind == JsonValueKind.String &&
                     !string.IsNullOrEmpty(passwordElement.GetString()))
            {
                if (passwordElement.GetString().Length > 1024)
                    return "OBS password is too long";
                updated.Password = passwordElement.GetString();
            }

            try
            {
                SaveSettings(updated);
            }
            catch (Exception ex)
            {
                _log("[obs] settings save failed: " + ex.Message);
                return "Unable to save OBS settings";
            }
            _settings = updated;
            _lastError = "";
            RestartConnectionLoop();
        }
        _log("[obs] settings updated; enabled=" + enabledElement.GetBoolean() + " endpoint=" + host + ":" + port);
        return null;
    }

    internal object TestConnection()
    {
        if (!_settings.Enabled) return new { ok = false, error = "Enable OBS before testing the connection" };
        if (!_connected) return new { ok = false, error = string.IsNullOrEmpty(_lastError) ? "OBS WebSocket is not connected yet" : _lastError };
        try
        {
            var response = SendRequestAsync("GetVersion", null, TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
            if (!IsSuccessful(response, out var error))
                return new { ok = false, error = error ?? "OBS GetVersion request failed" };
            var responseData = response.TryGetProperty("responseData", out var value) ? value : default;
            return new { ok = true, connected = true, version = ReadString(responseData, "obsVersion") };
        }
        catch (Exception ex) { return new { ok = false, error = ex.Message }; }
    }

    private async Task RunLoop(CancellationToken cancellationToken)
    {
        var delay = TimeSpan.FromSeconds(1);
        while (!cancellationToken.IsCancellationRequested)
        {
            ObsSettings settings;
            lock (_sync) settings = _settings;
            if (!settings.Enabled)
            {
                await Delay(delay, cancellationToken).ConfigureAwait(false);
                continue;
            }

            ClientWebSocket socket = null;
            try
            {
                socket = new ClientWebSocket();
                using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                connectTimeout.CancelAfter(TimeSpan.FromSeconds(5));
                await socket.ConnectAsync(new UriBuilder(Uri.UriSchemeWs, settings.Host, settings.Port, "/").Uri, connectTimeout.Token)
                    .ConfigureAwait(false);
                lock (_sync)
                {
                    if (cancellationToken.IsCancellationRequested || !ReferenceEquals(settings, _settings))
                    {
                        socket.Dispose();
                        continue;
                    }
                    _socket = socket;
                }
                _lastError = "";
                _log("[obs] websocket connected to " + settings.Host + ":" + settings.Port);
                await ReceiveLoop(socket, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                _log("[obs] connection failed: " + ex.Message);
            }
            finally
            {
                if (socket != null)
                {
                    lock (_sync)
                    {
                        if (ReferenceEquals(_socket, socket))
                        {
                            _socket = null;
                            _connected = false;
                        }
                    }
                    socket.Dispose();
                    FailPending("OBS connection closed");
                }
            }

            await Delay(delay, cancellationToken).ConfigureAwait(false);
            delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 30));
        }
    }

    private async Task ReceiveLoop(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        while (!cancellationToken.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            using var message = new MemoryStream();
            WebSocketReceiveResult receive;
            do
            {
                receive = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken).ConfigureAwait(false);
                if (receive.MessageType == WebSocketMessageType.Close) return;
                if (receive.MessageType != WebSocketMessageType.Text)
                    throw new IOException("OBS WebSocket sent a non-text message");
                message.Write(buffer, 0, receive.Count);
                if (message.Length > 1024 * 1024) throw new IOException("OBS WebSocket message exceeds 1 MB");
            } while (!receive.EndOfMessage);

            using var document = JsonDocument.Parse(message.ToArray());
            var root = document.RootElement;
            if (!root.TryGetProperty("op", out var opElement) || !opElement.TryGetInt32(out var op)) continue;
            if (op == 0) await Identify(socket, root, cancellationToken).ConfigureAwait(false);
            else if (op == 2)
            {
                lock (_sync)
                {
                    if (!ReferenceEquals(_socket, socket)) return;
                    _connected = true;
                    _lastError = "";
                }
                _log("[obs] websocket identified and ready");
                _ = Task.Run(() => SynchronizeStatus(socket));
            }
            else if (op == 5) HandleEvent(socket, root);
            else if (op == 7) HandleResponse(root);
        }
    }

    private async Task Identify(ClientWebSocket socket, JsonElement hello, CancellationToken cancellationToken)
    {
        var identify = new Dictionary<string, object> { ["rpcVersion"] = 1, ["eventSubscriptions"] = 1 << 6 };
        if (hello.TryGetProperty("d", out var data) &&
            data.TryGetProperty("authentication", out var auth) &&
            auth.ValueKind == JsonValueKind.Object)
        {
            var password = _settings.Password;
            if (!string.IsNullOrEmpty(password) &&
                auth.TryGetProperty("salt", out var salt) &&
                auth.TryGetProperty("challenge", out var challenge))
            {
                identify["authentication"] = BuildAuthentication(password, salt.GetString(), challenge.GetString());
            }
        }
        await SendPayload(socket, new { op = 1, d = identify }, cancellationToken).ConfigureAwait(false);
    }

    private async Task SendPayload(ClientWebSocket socket, object payload, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));
        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cancellationToken)
                .ConfigureAwait(false);
        }
        finally { _sendLock.Release(); }
    }

    private async Task<JsonElement> SendRequestAsync(string requestType, object requestData, TimeSpan timeout)
    {
        ClientWebSocket socket;
        lock (_sync) socket = _socket;
        if (!_connected || socket == null || socket.State != WebSocketState.Open)
            throw new InvalidOperationException("OBS WebSocket is not connected");

        var id = Interlocked.Increment(ref _requestId).ToString();
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;
        try
        {
            var data = new Dictionary<string, object> { ["requestType"] = requestType, ["requestId"] = id };
            if (requestData != null) data["requestData"] = requestData;
            await SendPayload(socket, new { op = 6, d = data }, CancellationToken.None).ConfigureAwait(false);
            using var timeoutSource = new CancellationTokenSource(timeout);
            using (timeoutSource.Token.Register(() => completion.TrySetCanceled(timeoutSource.Token)))
            {
                return await completion.Task.ConfigureAwait(false);
            }
        }
        finally { _pending.TryRemove(id, out _); }
    }

    private void HandleResponse(JsonElement root)
    {
        if (!root.TryGetProperty("d", out var data) ||
            !data.TryGetProperty("requestId", out var idElement)) return;
        var id = idElement.GetString();
        if (id != null && _pending.TryGetValue(id, out var completion))
            completion.TrySetResult(data.Clone());
    }

    private void HandleEvent(ClientWebSocket socket, JsonElement root)
    {
        if (!root.TryGetProperty("d", out var data) ||
            !data.TryGetProperty("eventType", out var typeElement)) return;
        var type = typeElement.GetString();
        if (type == null) return;
        var eventData = data.TryGetProperty("eventData", out var eventValue) ? eventValue : default;

        string selection = null;
        if (_settings.ForwardRecordStateChanged)
        {
            var state = ReadString(eventData, "outputState");
            if (type == "RecordStateChanged")
            {
                if (state == "OBS_WEBSOCKET_OUTPUT_STARTED") { _recording = true; selection = "RECORD_STARTED"; }
                else if (state == "OBS_WEBSOCKET_OUTPUT_STOPPED") { _recording = false; selection = "RECORD_STOPPED"; }
                else if (state == "OBS_WEBSOCKET_OUTPUT_PAUSED") { _recording = true; selection = "RECORD_PAUSED"; }
                else if (state == "OBS_WEBSOCKET_OUTPUT_RESUMED") { _recording = true; selection = "RECORD_RESUMED"; }
            }
            else if (type == "ReplayBufferStateChanged")
            {
                if (state == "OBS_WEBSOCKET_OUTPUT_STARTED") selection = "INSTANT_REPLAY_STARTED";
                else if (state == "OBS_WEBSOCKET_OUTPUT_STOPPED") selection = "INSTANT_REPLAY_STOPPED";
            }
            else if (type == "StreamStateChanged")
            {
                if (state == "OBS_WEBSOCKET_OUTPUT_STARTED") selection = "BROADCAST_STARTED";
                else if (state == "OBS_WEBSOCKET_OUTPUT_STOPPED") selection = "BROADCAST_STOPPED";
            }
        }
        if (selection != null)
        {
            _log("[obs] " + type + " -> " + selection);
            PublishNotification(new { selection, eventId = Guid.NewGuid().ToString("N") });
        }
        if (type == "ReplayBufferSaved")
        {
            var savedReplayPath = ReadString(eventData, "savedReplayPath");
            if (string.IsNullOrWhiteSpace(savedReplayPath))
            {
                PublishReplaySavedWithoutDuration("OBS did not provide savedReplayPath");
                return;
            }
            _log("[obs] ReplayBufferSaved -> " + savedReplayPath);
            _ = Task.Run(() => PublishReplaySaved(savedReplayPath));
        }
    }

    private void PublishReplaySaved(string savedReplayPath)
    {
        int seconds;
        try
        {
            seconds = WaitForSavedReplayDuration(savedReplayPath);
        }
        catch (Exception ex)
        {
            PublishReplaySavedWithoutDuration(ex.Message);
            return;
        }

        var minutes = seconds / 60;
        var remainingSeconds = seconds % 60;
        _log("[obs] replay clip duration " + FormatDuration(seconds) + " from " + savedReplayPath);
        PublishNotification(new
        {
            selection = "INSTANT_REPLAY_SAVED",
            arg1 = minutes,
            arg2 = remainingSeconds,
            eventId = Guid.NewGuid().ToString("N")
        });
    }

    private void PublishReplaySavedWithoutDuration(string reason)
    {
        _log("[obs] replay clip duration unavailable; showing save toast without duration: " + reason);
        PublishNotification(new
        {
            selection = "INSTANT_REPLAY_SAVED",
            durationUnavailable = true,
            eventId = Guid.NewGuid().ToString("N")
        });
    }

    private static int WaitForSavedReplayDuration(string savedReplayPath)
    {
        if (!Path.IsPathFullyQualified(savedReplayPath))
            throw new InvalidDataException("OBS returned a non-absolute replay path");

        long previousLength = -1;
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var fileInfo = new FileInfo(savedReplayPath);
            if (fileInfo.Exists && fileInfo.Length > 0 && fileInfo.Length == previousLength)
                return GetSavedReplayDurationSeconds(savedReplayPath);
            previousLength = fileInfo.Exists ? fileInfo.Length : -1;
            Thread.Sleep(TimeSpan.FromMilliseconds(250));
        }
        throw new IOException("Saved replay file did not become available and stable");
    }

    private static int GetSavedReplayDurationSeconds(string path)
    {
        lock (MediaFoundationSync)
        {
            Marshal.ThrowExceptionForHR(MFStartup(0x00020070, 0));
            IMediaFoundationSourceReader reader = null;
            MediaFoundationPropVariant duration = default;
            try
            {
                Marshal.ThrowExceptionForHR(MFCreateSourceReaderFromURL(path, IntPtr.Zero, out reader));
                var durationAttribute = new Guid("6C990D33-BB8E-477A-8598-0D5D96FCD88A");
                Marshal.ThrowExceptionForHR(reader.GetPresentationAttribute(
                    0xFFFFFFFF, ref durationAttribute, out duration));
                if ((duration.Type != 20 && duration.Type != 21) || duration.Int64Value <= 0)
                    throw new InvalidDataException("Media Foundation returned unsupported duration type " +
                        duration.Type + " and value " + duration.Int64Value);

                var seconds = Math.Round(duration.Int64Value / 10000000d, MidpointRounding.AwayFromZero);
                if (seconds < 1 || seconds > int.MaxValue)
                    throw new InvalidDataException("Saved replay clip duration is outside the supported range");
                return (int)seconds;
            }
            finally
            {
                PropVariantClear(ref duration);
                if (reader != null && Marshal.IsComObject(reader)) Marshal.ReleaseComObject(reader);
                MFShutdown();
            }
        }
    }

    [DllImport("mfplat.dll", ExactSpelling = true, PreserveSig = true)]
    private static extern int MFStartup(int version, int flags);

    [DllImport("mfreadwrite.dll", ExactSpelling = true, CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int MFCreateSourceReaderFromURL(string url, IntPtr attributes,
        [MarshalAs(UnmanagedType.Interface)] out IMediaFoundationSourceReader sourceReader);

    [DllImport("mfplat.dll", ExactSpelling = true, PreserveSig = true)]
    private static extern int MFShutdown();

    [DllImport("ole32.dll", ExactSpelling = true, PreserveSig = true)]
    private static extern int PropVariantClear(ref MediaFoundationPropVariant value);

    private static string FormatDuration(int totalSeconds) =>
        (totalSeconds / 60) + "m " + (totalSeconds % 60) + "s";

    private void PublishNotification(object notification) =>
        _publishEvent?.Invoke("/Duluka/v1/OBSNotification", notification);

    private void RestartConnectionLoop()
    {
        lock (_sync)
        {
            var old = _runCancellation;
            old?.Cancel();
            try { _socket?.Abort(); } catch { }
            _socket?.Dispose();
            _socket = null;
            _connected = false;
            var cancellation = new CancellationTokenSource();
            _runCancellation = cancellation;
            _runTask = Task.Run(() => RunLoop(cancellation.Token));
        }
        FailPending("OBS settings changed");
    }

    private ObsSettings ReadSettings()
    {
        try
        {
            if (!File.Exists(_configPath)) return new ObsSettings();
            using var document = JsonDocument.Parse(File.ReadAllText(_configPath));
            var root = document.RootElement;
            return new ObsSettings
            {
                Enabled = ReadBoolean(root, "enabled", false),
                Host = ReadString(root, "host") ?? "127.0.0.1",
                Port = ReadInt(root, "port", 4455),
                Password = ReadString(root, "password") ?? "",
                ForwardRecordStateChanged = ReadNestedBoolean(root, "forward", "record_state_changed", true)
            };
        }
        catch (Exception ex)
        {
            _log("[obs] config read failed; OBS remains disabled: " + ex.Message);
            return new ObsSettings();
        }
    }

    private void SaveSettings(ObsSettings settings)
    {
        JsonObject root = new JsonObject();
        if (File.Exists(_configPath))
        {
            root = JsonNode.Parse(File.ReadAllText(_configPath)) as JsonObject
                ?? throw new InvalidDataException("OBS config must contain a JSON object");
        }
        root["enabled"] = settings.Enabled;
        root["host"] = settings.Host;
        root["port"] = settings.Port;
        root["password"] = settings.Password;
        root["forward"] = new JsonObject
        {
            ["record_state_changed"] = settings.ForwardRecordStateChanged
        };
        var directory = Path.GetDirectoryName(_configPath);
        Directory.CreateDirectory(directory);
        var temporary = _configPath + "." + Environment.ProcessId + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, _configPath, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static string BuildAuthentication(string password, string salt, string challenge)
    {
        var salted = SHA256.HashData(Encoding.UTF8.GetBytes(password + salt));
        var secret = Convert.ToBase64String(salted);
        var auth = SHA256.HashData(Encoding.UTF8.GetBytes(secret + challenge));
        return Convert.ToBase64String(auth);
    }

    private static bool IsSuccessful(JsonElement response, out string error)
    {
        error = null;
        if (response.ValueKind == JsonValueKind.Undefined) { error = "OBS returned no response"; return false; }
        if (!response.TryGetProperty("requestStatus", out var status) ||
            !status.TryGetProperty("result", out var result) ||
            result.ValueKind != JsonValueKind.True)
        {
            error = status.ValueKind == JsonValueKind.Object &&
                status.TryGetProperty("comment", out var comment) ? comment.GetString() : "OBS request failed";
            return false;
        }
        return true;
    }

    private static bool ReadBoolean(JsonElement element, string key, bool fallback)
    {
        return element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(key, out var property) &&
            (property.ValueKind == JsonValueKind.True || property.ValueKind == JsonValueKind.False)
                ? property.GetBoolean() : fallback;
    }

    private static bool ReadNestedBoolean(JsonElement root, string section, string key, bool fallback)
    {
        return root.TryGetProperty(section, out var nested) ? ReadBoolean(nested, key, fallback) : fallback;
    }

    private static int ReadInt(JsonElement element, string key, int fallback) =>
        element.TryGetProperty(key, out var property) && property.TryGetInt32(out var value) ? value : fallback;

    private static string ReadString(JsonElement element, string key) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(key, out var value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static async Task Delay(TimeSpan delay, CancellationToken cancellationToken)
    {
        try { await Task.Delay(delay, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
    }

    private void FailPending(string message)
    {
        foreach (var request in _pending.ToArray())
        {
            if (_pending.TryRemove(request.Key, out var completion))
                completion.TrySetException(new IOException(message));
        }
    }

    public void Dispose()
    {
        CancellationTokenSource cancellation;
        lock (_sync)
        {
            cancellation = _runCancellation;
            _runCancellation = null;
            try { _socket?.Abort(); } catch { }
            _socket?.Dispose();
            _socket = null;
            _connected = false;
        }
        cancellation?.Cancel();
        FailPending("OBS runtime stopped");
    }
}
