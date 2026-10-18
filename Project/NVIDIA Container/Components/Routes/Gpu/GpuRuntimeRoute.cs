using System.Text.Json;
using NvGraphics.Contracts;



/// <summary>
/// POST /v1/gpu/runtime — receives NvGraphics runtime GPU state and forwards it under
/// the FIXED event name GpuRuntimeContract.EventName. The client can never choose the
/// event name, and nothing but this validated payload is forwarded (no secrets).
/// Pure handler logic (no sockets) so tests can exercise the exact production path.
/// </summary>
// Global-namespace convention matches NvContainer.cs / ContainerTestApi.cs.
internal static class GpuRuntimeRoute
{
    public static (int Status, object Response) Handle(
        byte[] body,
        Action<JsonElement> publishEvents,
        Action<string> log,
        DateTimeOffset? now = null)
    {
        if (!GpuRuntimePayloadValidator.TryValidate(body, now ?? DateTimeOffset.UtcNow,
                out var payload, out var error, out int status))
        {
            log("[gpu-bridge] rejected payload: " + error);
            return (status, new { error });
        }

        // Forward under the fixed event name via the existing ContainerEvents lane
        // (camelCase JSON to match the lane's consumers, e.g. event.data.hotkeyId).
        // NOTE: until NodeAPI's whitelist includes GpuRuntimeContract.EventName this
        // publish is accepted here but silently dropped by NodeAPI — by design (Phase 3B).
        var forwarded = new object[] { new { name = GpuRuntimeContract.EventName, data = payload } };
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(forwarded,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        publishEvents?.Invoke(document.RootElement.Clone());

        log("[gpu-bridge] accepted " + payload.State + " for " +
            (string.IsNullOrEmpty(payload.DisplayName) ? payload.ExePath : payload.DisplayName) +
            " (eventId " + payload.EventId + ", window " + payload.WindowDurationMs + "ms)");
        return (200, new { accepted = true, eventName = GpuRuntimeContract.EventName, eventId = payload.EventId });
    }

    /// <summary>POST /v1/gpu/preference — GPU-preference-applied event (separate lane
    /// from runtime results; reports a verified registry write, not actual usage).</summary>
    public static (int Status, object Response) HandlePreference(
        byte[] body, Action<JsonElement> publishEvents, Action<string> log, DateTimeOffset? now = null)
    {
        if (!GpuPreferencePayloadValidator.TryValidate(body, now ?? DateTimeOffset.UtcNow,
                out var payload, out var error, out int status))
        {
            log("[gpu-bridge] rejected preference payload: " + error);
            return (status, new { error });
        }

        var forwarded = new object[] { new { name = GpuPreferenceContract.EventName, data = payload } };
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(forwarded,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        publishEvents?.Invoke(document.RootElement.Clone());

        log("[gpu-bridge] accepted preference " +
            (payload.IsSystemDefault ? "system default" : payload.AdapterId) + " for " +
            (string.IsNullOrEmpty(payload.DisplayName) ? payload.ExePath : payload.DisplayName) +
            " (eventId " + payload.EventId + ")");
        return (200, new { accepted = true, eventName = GpuPreferenceContract.EventName, eventId = payload.EventId });
    }

    public static (int Status, object Response) HandleInventory(
        byte[] body, Action<JsonElement> publishEvents, Action<string> log, DateTimeOffset? now = null)
    {
        if (!GpuAdapterInventoryPayloadValidator.TryValidate(body, now ?? DateTimeOffset.UtcNow,
                out var payload, out var error, out int status))
        {
            log("[gpu-bridge] rejected adapter inventory: " + error);
            return (status, new { error });
        }

        var forwarded = new object[] { new { name = GpuAdapterInventoryContract.EventName, data = payload } };
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(forwarded,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        publishEvents?.Invoke(document.RootElement.Clone());
        log("[gpu-bridge] accepted adapter inventory (" + payload.Adapters.Count +
            " adapters, eventId " + payload.EventId + ")");
        return (200, new
        {
            accepted = true,
            eventName = GpuAdapterInventoryContract.EventName,
            eventId = payload.EventId
        });
    }
}
