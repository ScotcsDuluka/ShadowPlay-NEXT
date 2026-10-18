using System.Text.Json;

namespace NvGraphics.Contracts;

/// <summary>Versioned inventory snapshot for the OSC GPU status panel.</summary>
public sealed class GpuAdapterInventoryPayload
{
    public int SchemaVersion { get; set; }
    public string EventId { get; set; } = "";
    public DateTimeOffset CapturedUtc { get; set; }
    public List<GpuAdapterInventoryEntry> Adapters { get; set; } = new();
}

public sealed class GpuAdapterInventoryEntry
{
    public string Name { get; set; } = "";
    public string AdapterId { get; set; } = "";
    public string PciBusId { get; set; } = "";
    public List<string> DxgiLuids { get; set; } = new();
    public double? UtilizationPercent { get; set; }
    public double? MemoryUsedMiB { get; set; }
    public double? MemoryTotalMiB { get; set; }
}

public static class GpuAdapterInventoryContract
{
    public const int CurrentSchemaVersion = 1;
    public const string RoutePath = "/v1/gpu/inventory";
    public const string EventName = "/Duluka/v1/GpuAdapterInventory";
    public const int MaxPayloadBytes = 32768;
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(2);

    public static string Serialize(GpuAdapterInventoryPayload payload) =>
        JsonSerializer.Serialize(payload, new System.Text.Json.JsonSerializerOptions
        {
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
        });
}

public static class GpuAdapterInventoryPayloadValidator
{
    public static bool TryValidate(byte[] body, DateTimeOffset now,
        out GpuAdapterInventoryPayload payload, out string error, out int httpStatus)
    {
        payload = null;
        error = null;
        httpStatus = 200;
        if (body is null || body.Length == 0)
        { error = "empty body"; httpStatus = 400; return false; }
        if (body.Length > GpuAdapterInventoryContract.MaxPayloadBytes)
        { error = "payload too large"; httpStatus = 413; return false; }
        try
        {
            payload = JsonSerializer.Deserialize<GpuAdapterInventoryPayload>(body,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException)
        { error = "invalid JSON"; httpStatus = 400; return false; }

        if (payload is null || payload.SchemaVersion != GpuAdapterInventoryContract.CurrentSchemaVersion)
        { error = "unsupported or missing schemaVersion"; httpStatus = 400; payload = null; return false; }
        if (string.IsNullOrWhiteSpace(payload.EventId) || payload.EventId.Length > 64)
        { error = "eventId must be 1-64 characters"; httpStatus = 400; return false; }
        if (payload.CapturedUtc == default)
        { error = "capturedUtc is required"; httpStatus = 400; return false; }
        var age = now - payload.CapturedUtc;
        if (age > GpuAdapterInventoryContract.StaleAfter || age < -GpuAdapterInventoryContract.StaleAfter)
        { error = "stale inventory snapshot"; httpStatus = 422; return false; }
        if (payload.Adapters is null || payload.Adapters.Count > 16)
        { error = "invalid adapters"; httpStatus = 400; return false; }
        foreach (var adapter in payload.Adapters)
        {
            if (adapter is null || string.IsNullOrWhiteSpace(adapter.Name) || adapter.Name.Length > 256 ||
                (adapter.AdapterId?.Length ?? 0) > 128 || (adapter.PciBusId?.Length ?? 0) > 32 ||
                adapter.DxgiLuids is null || adapter.DxgiLuids.Count > 8 ||
                adapter.DxgiLuids.Any(luid => string.IsNullOrWhiteSpace(luid) || luid.Length > 64) ||
                !ValidPercent(adapter.UtilizationPercent) ||
                !ValidMemory(adapter.MemoryUsedMiB) || !ValidMemory(adapter.MemoryTotalMiB))
            { error = "invalid adapter entry"; httpStatus = 400; return false; }
            if (adapter.MemoryUsedMiB.HasValue && adapter.MemoryTotalMiB.HasValue &&
                adapter.MemoryUsedMiB > adapter.MemoryTotalMiB)
            { error = "memory used exceeds total"; httpStatus = 400; return false; }
        }
        return true;
    }

    private static bool ValidPercent(double? value) =>
        !value.HasValue || (double.IsFinite(value.Value) && value.Value >= 0 && value.Value <= 100);

    private static bool ValidMemory(double? value) =>
        !value.HasValue || (double.IsFinite(value.Value) && value.Value >= 0 && value.Value <= 1_000_000);
}
