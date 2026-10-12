using NvGraphics.Models;

namespace NvGraphics.Abstractions;

/// <summary>
/// Source of per-process GPU engine utilization samples (normally the Windows
/// "GPU Engine" performance counter set). Injectable so tests need no live GPU.
/// </summary>
public interface IEngineCounterProvider
{
    string SourceName { get; }
    /// <summary>Collects one snapshot of samples. Returns false with a reason when the
    /// counter set is missing or collection fails — never fabricates samples.</summary>
    bool TryCollect(List<EngineSample> samples, out string error);
}

/// <summary>Maps a GPU Engine counter LUID string (e.g. "0x00000000_0x00013667")
/// to a previously discovered physical adapter, or null when unresolved.</summary>
public interface ILuidAdapterResolver
{
    GpuAdapterInfo Resolve(string luid);
}

/// <summary>Resolves a live PID to its full executable path; null on access-denied or exit.</summary>
public interface IProcessPathResolver
{
    string TryGetPath(int pid);
}
