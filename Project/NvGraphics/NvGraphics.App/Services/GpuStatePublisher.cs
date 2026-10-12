using System.Net.Http;
using System.Text;
using System.Text.Json;
using NvGraphics.Contracts;
using NvGraphics.Models;

namespace NvGraphics.Services;

public sealed class GpuStatePublishResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = "";
    /// <summary>True when NvContainer accepted the payload. Acceptance does NOT mean
    /// OSC delivery — the NodeAPI whitelist does not include the GPU event yet.</summary>
    public bool AcceptedByContainer { get; init; }
}

/// <summary>
/// Sends runtime GPU results to NvContainer's loopback control API
/// (POST /v1/gpu/runtime). Detection stays in RuntimeGpuMonitorService; this class
/// only transports results, so it is fully testable with a fake HTTP handler.
/// No polling loop — one PublishAsync call per detection result.
/// </summary>
public sealed class GpuStatePublisher
{
    private readonly HttpClient _http;
    private readonly int _port;
    public int Port => _port;

    public GpuStatePublisher(HttpClient http = null, int? port = null)
    {
        _http = http ?? new HttpClient();
        _port = port ?? DiscoverControlApiPort();
    }

    /// <summary>Finds NvConfig/nvcontainer.json by walking up from the app base
    /// directory (same discovery pattern as the NodeAPI entry point); falls back to
    /// the documented default 59012.</summary>
    public static int DiscoverControlApiPort(int defaultPort = 59012)
    {
        try
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            for (int depth = 0; depth < 10 && dir != null; depth++, dir = dir.Parent)
            {
                var cfg = Path.Combine(dir.FullName, "NvConfig", "nvcontainer.json");
                if (!File.Exists(cfg)) continue;
                using var doc = JsonDocument.Parse(File.ReadAllText(cfg));
                if (doc.RootElement.TryGetProperty("controlApiPort", out var node) &&
                    node.ValueKind == JsonValueKind.Number && node.TryGetInt32(out int p) &&
                    p > 0 && p <= 65535)
                    return p;
                break;
            }
        }
        catch
        {
            // Config unreadable — fall back to the default.
        }
        return defaultPort;
    }

    /// <summary>Builds a payload from a real detection result. Never derives adapters
    /// from a Windows preference and never invents adapter identity.</summary>
    public static GpuRuntimePayload FromResult(RuntimeGpuResult result, string exePath, string displayName, int pid = 0)
    {
        if (result is null) throw new ArgumentNullException(nameof(result));
        var evidencePid = result.Evidence.Count > 0 ? result.Evidence[0].Pid : pid;
        return new GpuRuntimePayload
        {
            SchemaVersion = GpuRuntimeContract.CurrentSchemaVersion,
            EventId = Guid.NewGuid().ToString("N"),
            ExePath = exePath,
            DisplayName = displayName ?? "",
            Pid = evidencePid,
            State = result.State.ToString(),
            Adapters = result.Adapters.Select(a => new GpuRuntimeAdapter
            {
                Name = a.DisplayName ?? "",
                AdapterId = a.AdapterId ?? "",
                PciBusId = a.PciBusId ?? ""
            }).ToList(),
            EngineTypes = result.EngineTypesObserved?.ToList() ?? new(),
            WindowStartUtc = result.WindowStartUtc,
            WindowDurationMs = (int)Math.Min(int.MaxValue, Math.Max(0, result.WindowDuration.TotalMilliseconds)),
            Reason = result.Reason ?? ""
        };
    }

    /// <summary>Sends one payload. Async all the way — callers must not block the UI
    /// thread on it. Failures are reported, never thrown.</summary>
    public async Task<GpuStatePublishResult> PublishAsync(GpuRuntimePayload payload, TimeSpan? timeout = null)
    {
        if (payload is null)
            return new GpuStatePublishResult { Message = "no payload" };
        try
        {
            using var cts = new System.Threading.CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(3));
            using var content = new StringContent(GpuRuntimeContract.Serialize(payload), Encoding.UTF8, "application/json");
            var response = await _http.PostAsync(
                $"http://127.0.0.1:{_port}{GpuRuntimeContract.RoutePath}", content, cts.Token)
                .ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            bool accepted = response.IsSuccessStatusCode;
            return new GpuStatePublishResult
            {
                Success = accepted,
                AcceptedByContainer = accepted,
                Message = accepted
                    ? $"GPU state delivered to NvContainer (HTTP {(int)response.StatusCode}) → OSC event {GpuRuntimeContract.EventName}."
                    : $"NvContainer rejected the result: HTTP {(int)response.StatusCode} {Truncate(body, 200)}"
            };
        }
        catch (OperationCanceledException)
        {
            return new GpuStatePublishResult { Message = "publish timed out (NvContainer did not respond within the timeout)" };
        }
        catch (HttpRequestException ex)
        {
            return new GpuStatePublishResult { Message = $"NvContainer unreachable on port {_port}: {ex.Message}" };
        }
        catch (Exception ex)
        {
            return new GpuStatePublishResult { Message = $"publish failed: {ex.Message}" };
        }
    }

    /// <summary>Sends one GPU-preference-applied event (separate lane from runtime
    /// results — reports the verified registry write, never actual GPU usage).</summary>
    public async Task<GpuStatePublishResult> PublishPreferenceAsync(GpuPreferencePayload payload, TimeSpan? timeout = null)
    {
        if (payload is null)
            return new GpuStatePublishResult { Message = "no payload" };
        try
        {
            using var cts = new System.Threading.CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(3));
            using var content = new StringContent(
                JsonSerializer.Serialize(payload, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }),
                Encoding.UTF8, "application/json");
            var response = await _http.PostAsync(
                $"http://127.0.0.1:{_port}{GpuPreferenceContract.RoutePath}", content, cts.Token)
                .ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            bool accepted = response.IsSuccessStatusCode;
            return new GpuStatePublishResult
            {
                Success = accepted,
                AcceptedByContainer = accepted,
                Message = accepted
                    ? $"Preference event delivered (HTTP {(int)response.StatusCode}) → OSC event {GpuPreferenceContract.EventName}."
                    : $"NvContainer rejected the preference event: HTTP {(int)response.StatusCode} {Truncate(body, 200)}"
            };
        }
        catch (OperationCanceledException)
        {
            return new GpuStatePublishResult { Message = "preference publish timed out" };
        }
        catch (HttpRequestException ex)
        {
            return new GpuStatePublishResult { Message = $"NvContainer unreachable on port {_port}: {ex.Message}" };
        }
        catch (Exception ex)
        {
            return new GpuStatePublishResult { Message = $"preference publish failed: {ex.Message}" };
        }
    }

    public async Task<GpuStatePublishResult> PublishInventoryAsync(
        GpuAdapterInventoryPayload payload, TimeSpan? timeout = null)
    {
        if (payload is null)
            return new GpuStatePublishResult { Message = "no inventory payload" };
        try
        {
            using var cts = new System.Threading.CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(3));
            using var content = new StringContent(
                GpuAdapterInventoryContract.Serialize(payload), Encoding.UTF8, "application/json");
            var response = await _http.PostAsync(
                $"http://127.0.0.1:{_port}{GpuAdapterInventoryContract.RoutePath}", content, cts.Token)
                .ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            bool accepted = response.IsSuccessStatusCode;
            return new GpuStatePublishResult
            {
                Success = accepted,
                AcceptedByContainer = accepted,
                Message = accepted
                    ? $"GPU adapter inventory delivered (HTTP {(int)response.StatusCode}) → OSC event {GpuAdapterInventoryContract.EventName}."
                    : $"NvContainer rejected the adapter inventory: HTTP {(int)response.StatusCode} {Truncate(body, 200)}"
            };
        }
        catch (OperationCanceledException)
        {
            return new GpuStatePublishResult { Message = "GPU inventory publish timed out" };
        }
        catch (HttpRequestException ex)
        {
            return new GpuStatePublishResult { Message = $"NvContainer unreachable on port {_port}: {ex.Message}" };
        }
        catch (Exception ex)
        {
            return new GpuStatePublishResult { Message = $"GPU inventory publish failed: {ex.Message}" };
        }
    }

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s.Substring(0, max) + "…");
}
