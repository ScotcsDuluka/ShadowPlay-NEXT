using System.Text.Json;

namespace NvGraphics.Contracts;

/// <summary>
/// Versioned NvGraphics → NvContainer GPU-preference-applied contract.
/// Endpoint: POST http://127.0.0.1:{controlApiPort}/v1/gpu/preference
/// Fixed forwarding event name: /Duluka/v1/GpuPreferenceApplied
/// This is a SEPARATE event from runtime results — it reports that the Windows
/// per-app GPU preference was written and verified, never that the game is
/// running on that GPU.
/// </summary>
public sealed class GpuPreferencePayload
{
    public int SchemaVersion { get; set; }
    public string EventId { get; set; } = "";
    public string ExePath { get; set; } = "";
    public string DisplayName { get; set; } = "";
    /// <summary>Windows-form adapter id (e.g. 10DE&amp;1B06&amp;85F11043) that was applied.</summary>
    public string AdapterId { get; set; } = "";
    /// <summary>Human-readable adapter name as selected in NvGraphics.</summary>
    public string AdapterName { get; set; } = "";
    /// <summary>True when the per-app GPU override was removed and Windows uses its default.</summary>
    public bool IsSystemDefault { get; set; }
    public DateTimeOffset AppliedUtc { get; set; }
}

public static class GpuPreferenceContract
{
    public const int CurrentSchemaVersion = 1;
    public const string RoutePath = "/v1/gpu/preference";
    public const string EventName = "/Duluka/v1/GpuPreferenceApplied";
    public const int MaxPayloadBytes = 16384;
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(2);
}

public static class GpuPreferencePayloadValidator
{
    public static bool TryValidate(byte[] body, DateTimeOffset now,
        out GpuPreferencePayload payload, out string error, out int httpStatus)
    {
        payload = null;
        error = null;
        httpStatus = 200;

        if (body is null || body.Length == 0)
        { error = "empty body"; httpStatus = 400; return false; }
        if (body.Length > GpuPreferenceContract.MaxPayloadBytes)
        { error = "payload exceeds " + GpuPreferenceContract.MaxPayloadBytes + " bytes"; httpStatus = 413; return false; }

        try
        {
            payload = JsonSerializer.Deserialize<GpuPreferencePayload>(body,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException)
        { error = "invalid JSON"; httpStatus = 400; payload = null; return false; }

        if (payload is null)
        { error = "invalid JSON payload"; httpStatus = 400; return false; }
        if (payload.SchemaVersion != GpuPreferenceContract.CurrentSchemaVersion)
        { error = "unsupported schemaVersion " + payload.SchemaVersion; httpStatus = 400; return false; }
        if (string.IsNullOrWhiteSpace(payload.EventId) || payload.EventId.Length > 64)
        { error = "eventId must be 1-64 characters"; httpStatus = 400; return false; }
        if (string.IsNullOrWhiteSpace(payload.ExePath) || payload.ExePath.Length > 512 || payload.ExePath.Length < 3)
        { error = "exePath must be an absolute path"; httpStatus = 400; return false; }
        if ((!payload.IsSystemDefault && string.IsNullOrWhiteSpace(payload.AdapterId)) ||
            (payload.AdapterId?.Length ?? 0) > 128)
        { error = "adapterId is required unless isSystemDefault is true"; httpStatus = 400; return false; }
        if ((payload.AdapterName?.Length ?? 0) > 256 || (payload.DisplayName?.Length ?? 0) > 128)
        { error = "name fields too long"; httpStatus = 400; return false; }
        if (payload.AppliedUtc == default)
        { error = "appliedUtc is required"; httpStatus = 400; return false; }

        var age = now - payload.AppliedUtc;
        if (age > GpuPreferenceContract.StaleAfter || age < -GpuPreferenceContract.StaleAfter)
        { error = "stale event (appliedUtc outside the freshness window)"; httpStatus = 422; return false; }

        return true;
    }
}
