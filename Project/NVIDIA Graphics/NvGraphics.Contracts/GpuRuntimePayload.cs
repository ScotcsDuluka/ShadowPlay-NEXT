using System.Text.Json;
using System.Text.Json.Serialization;

namespace NvGraphics.Contracts;

/// <summary>
/// Versioned NvGraphics → NvContainer runtime-GPU-state contract.
/// Endpoint: POST http://127.0.0.1:{controlApiPort}/v1/gpu/runtime
/// NvContainer maps every accepted payload to the FIXED event name
/// GpuRuntimeContract.EventName ("/Duluka/v1/GpuRuntimeState") — clients can never
/// choose the event name. As of the Phase 3B bridge, NodeAPI's whitelist does NOT yet
/// include that event name, so acceptance by NvContainer does not imply OSC delivery.
/// </summary>
public sealed class GpuRuntimePayload
{
    public int SchemaVersion { get; set; }
    /// <summary>Unique id per published result; consumers deduplicate on this.</summary>
    public string EventId { get; set; } = "";
    /// <summary>Full executable path of the game this result belongs to.</summary>
    public string ExePath { get; set; } = "";
    /// <summary>Display name of the game, when known; never fabricated.</summary>
    public string DisplayName { get; set; } = "";
    /// <summary>Process ID of the sampled process, 0 when no target process matched.</summary>
    public int Pid { get; set; }
    /// <summary>Unknown | Inactive | Verified | MultipleAdapters (see GpuRuntimeContract.States).</summary>
    public string State { get; set; } = "";
    /// <summary>Adapters with verified runtime evidence. Empty unless state is
    /// Verified/MultipleAdapters — never populated from a Windows preference.</summary>
    public List<GpuRuntimeAdapter> Adapters { get; set; } = new();
    /// <summary>Engine types that supplied evidence (e.g. "3d", "copy").</summary>
    public List<string> EngineTypes { get; set; } = new();
    /// <summary>Start of the sampling window this result was computed from (UTC).</summary>
    public DateTimeOffset WindowStartUtc { get; set; }
    public int WindowDurationMs { get; set; }
    /// <summary>Machine-readable diagnostic reason (always present for Unknown).</summary>
    public string Reason { get; set; } = "";
}

public sealed class GpuRuntimeAdapter
{
    /// <summary>Discovered display name of the physical adapter.</summary>
    public string Name { get; set; } = "";
    /// <summary>PnP identity, e.g. VEN_10DE&amp;DEV_1B06&amp;SUBSYS_85F11043.</summary>
    public string AdapterId { get; set; } = "";
    /// <summary>PCI bus id e.g. 00000000:01:00.0, when available.</summary>
    public string PciBusId { get; set; } = "";
}

public static class GpuRuntimeContract
{
    public const int CurrentSchemaVersion = 1;
    public const string RoutePath = "/v1/gpu/runtime";
    /// <summary>Fixed forwarding event name chosen by NvContainer, not by clients.</summary>
    public const string EventName = "/Duluka/v1/GpuRuntimeState";
    /// <summary>Application-layer payload cap (transport layer caps at 256 KiB).</summary>
    public const int MaxPayloadBytes = 65536;
    /// <summary>Results with a sampling window older than this are rejected as stale.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(2);

    public static readonly HashSet<string> States = new(StringComparer.Ordinal)
        { "Unknown", "Inactive", "Verified", "MultipleAdapters" };

    public static string Serialize(GpuRuntimePayload payload) =>
        JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
}
