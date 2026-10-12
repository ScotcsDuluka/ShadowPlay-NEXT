using System.Text.Json;

namespace NvGraphics.Contracts;

/// <summary>
/// Receiver-side validation for POST /v1/gpu/runtime. Pure logic shared by the
/// NvContainer route and the test suite — no sockets, no state.
/// On success returns a normalized payload clone (never trusts client field extras).
/// </summary>
public static class GpuRuntimePayloadValidator
{
    public static bool TryValidate(byte[] body, DateTimeOffset now,
        out GpuRuntimePayload payload, out string error, out int httpStatus)
    {
        payload = null;
        error = null;
        httpStatus = 200;

        if (body is null || body.Length == 0)
        { error = "empty body"; httpStatus = 400; return false; }
        if (body.Length > GpuRuntimeContract.MaxPayloadBytes)
        { error = "payload exceeds " + GpuRuntimeContract.MaxPayloadBytes + " bytes"; httpStatus = 413; return false; }

        GpuRuntimePayload candidate;
        try
        {
            candidate = JsonSerializer.Deserialize<GpuRuntimePayload>(
                body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException ex)
        { error = "invalid JSON: " + ex.GetType().Name; httpStatus = 400; return false; }

        if (candidate is null)
        { error = "invalid JSON payload"; httpStatus = 400; return false; }
        if (candidate.SchemaVersion != GpuRuntimeContract.CurrentSchemaVersion)
        { error = "unsupported schemaVersion " + candidate.SchemaVersion; httpStatus = 400; return false; }
        if (string.IsNullOrWhiteSpace(candidate.EventId) || candidate.EventId.Length > 64)
        { error = "eventId must be 1-64 characters"; httpStatus = 400; return false; }
        if (string.IsNullOrWhiteSpace(candidate.State) ||
            !GpuRuntimeContract.States.Contains(candidate.State))
        { error = "invalid state"; httpStatus = 400; return false; }
        if (string.IsNullOrWhiteSpace(candidate.ExePath) || !IsPlausiblePath(candidate.ExePath))
        { error = "exePath must be an absolute path"; httpStatus = 400; return false; }
        if (candidate.DisplayName?.Length > 128) { error = "displayName too long"; httpStatus = 400; return false; }
        if (candidate.Pid < 0 || candidate.Pid > 4194304) { error = "invalid pid"; httpStatus = 400; return false; }
        if (candidate.Reason is null || candidate.Reason.Length > 2048)
        { error = "reason too long"; httpStatus = 400; return false; }
        if (candidate.EngineTypes is null || candidate.EngineTypes.Count > 16
            || candidate.EngineTypes.Any(t => t is null || t.Length > 32))
        { error = "invalid engineTypes"; httpStatus = 400; return false; }
        if (candidate.WindowDurationMs < 0 || candidate.WindowDurationMs > 600000)
        { error = "invalid windowDurationMs"; httpStatus = 400; return false; }
        if (candidate.WindowStartUtc == default)
        { error = "windowStartUtc is required"; httpStatus = 400; return false; }

        // Stale-data policy: a sampling window older than StaleAfter is rejected so a
        // dead publisher's last result cannot be re-emitted as though it were fresh.
        var age = now - candidate.WindowStartUtc;
        if (age > GpuRuntimeContract.StaleAfter)
        { error = "stale result (window older than " + (int)GpuRuntimeContract.StaleAfter.TotalMinutes + " minutes)"; httpStatus = 422; return false; }
        if (age < -GpuRuntimeContract.StaleAfter)
        { error = "windowStartUtc is too far in the future (clock skew?)"; httpStatus = 422; return false; }

        // Adapters: only meaningful for Verified/MultipleAdapters; identity fields bounded.
        if (candidate.Adapters is null || candidate.Adapters.Count > 8)
        { error = "invalid adapters"; httpStatus = 400; return false; }
        foreach (var a in candidate.Adapters)
        {
            if (a is null || string.IsNullOrWhiteSpace(a.AdapterId) || a.AdapterId.Length > 128 ||
                (a.Name?.Length ?? 0) > 256 || (a.PciBusId?.Length ?? 0) > 32)
            { error = "invalid adapter record"; httpStatus = 400; return false; }
        }
        if (candidate.State == "Verified" && candidate.Adapters.Count != 1)
        { error = "Verified requires exactly one adapter"; httpStatus = 400; return false; }
        if (candidate.State == "MultipleAdapters" && candidate.Adapters.Count < 2)
        { error = "MultipleAdapters requires at least two adapters"; httpStatus = 400; return false; }
        if ((candidate.State == "Unknown" || candidate.State == "Inactive") && candidate.Adapters.Count > 0)
        { error = "state " + candidate.State + " must not carry adapter evidence"; httpStatus = 400; return false; }
        if (candidate.State == "Unknown" && string.IsNullOrWhiteSpace(candidate.Reason))
        { error = "Unknown requires a reason"; httpStatus = 400; return false; }

        payload = candidate;
        return true;
    }

    private static bool IsPlausiblePath(string path) =>
        path.Length <= 512 && path.Contains('\\') &&
        (path.Length > 2 && (path[1] == ':' || path.StartsWith(@"\\", StringComparison.Ordinal)));
}
