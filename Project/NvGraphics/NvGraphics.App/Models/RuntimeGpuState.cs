using NvGraphics.Models;

namespace NvGraphics.Models;

/// <summary>
/// Outcome of runtime GPU detection for one target executable over one sampling window.
/// A configured Windows GPU preference never influences these states.
/// </summary>
public enum RuntimeGpuState
{
    /// <summary>Evidence unavailable, inaccessible, stale, unmapped, or insufficient.</summary>
    Unknown,
    /// <summary>A matching process was observed across the whole window with valid telemetry
    /// and no meaningful activity. NOT proof that the game never uses a GPU.</summary>
    Inactive,
    /// <summary>Active 3D-engine usage attributed to exactly one discovered physical adapter.</summary>
    Verified,
    /// <summary>Relevant (3D) activity attributed to two or more discovered physical adapters.</summary>
    MultipleAdapters
}

/// <summary>One parsed GPU Engine counter observation.</summary>
public sealed record EngineSample(
    int Pid,
    string Luid,
    string EngineType,
    double UtilizationPercent,
    DateTimeOffset TimestampUtc);

/// <summary>Adapter attribution for one process over the sampling window.</summary>
public sealed record ProcessGpuEvidence(
    int Pid,
    string ExePathOrNull,
    IReadOnlyList<GpuAdapterInfo> Adapters,
    /// <summary>Engine types that supplied evidence, e.g. "3d", "copy".</summary>
    IReadOnlyCollection<string> EngineTypes,
    /// <summary>Max per-sample, per-adapter aggregated utilization percent seen in the window.</summary>
    double PeakAggregatedPercent);

/// <summary>Final runtime detection result with machine-readable reason.</summary>
public sealed record RuntimeGpuResult(
    RuntimeGpuState State,
    string Reason,
    IReadOnlyList<GpuAdapterInfo> Adapters,
    IReadOnlyList<ProcessGpuEvidence> Evidence,
    IReadOnlyCollection<string> EngineTypesObserved,
    DateTimeOffset WindowStartUtc,
    TimeSpan WindowDuration);
