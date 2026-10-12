using NvGraphics.Abstractions;
using NvGraphics.Models;

namespace NvGraphics.Services;

/// <summary>Tunable detection parameters (all defaults overridable for tests).</summary>
public sealed class RuntimeGpuMonitorOptions
{
    /// <summary>Samples aggregated per result. Default 3 (≈3 s at a 1 s interval).</summary>
    public int WindowSamples { get; set; } = 3;
    /// <summary>Delay between samples; 0 in tests.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);
    /// <summary>
    /// Per-sample, per-adapter aggregated utilization (%) at or above which activity counts.
    /// Rounding: values are compared exactly; a rounded-to-0 counter value is NOT activity.
    /// </summary>
    public double ActivityThresholdPercent { get; set; } = 0.5;
    /// <summary>How long before a resolved PID's path cache entry is re-validated.</summary>
    public TimeSpan PathCacheTtl { get; set; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// Produces RuntimeGpuResult for a target executable by aggregating GPU Engine samples
/// over a rolling window.
///
/// Aggregation rule (documented, the only sum performed): engine utilizations of ONE
/// adapter reported by ONE process in ONE sample are summed and clamped to 100 % —
/// engine counters of a single GPU are time-sliced against the same hardware budget, so
/// the sum approximates the adapter's load attributable to that process at that instant.
/// Across samples, the MAX of those per-sample sums is the window peak; sums are never
/// accumulated across time, adapters, or processes.
///
/// 3D-engine activity is the primary rendering evidence; auxiliary engines alone
/// (copy/videoencode/videodecode/…) yield Unknown. MultipleAdapters requires 3D
/// activity on multiple physical adapters in the same sample; seeing adapters in
/// different samples is reported as Unknown because it may be sequential switching.
/// </summary>
public sealed class RuntimeGpuMonitorService
{
    private readonly IEngineCounterProvider _counters;
    private readonly ILuidAdapterResolver _luidResolver;
    private readonly IProcessPathResolver _pathResolver;
    private readonly RuntimeGpuMonitorOptions _options;
    private readonly Action<TimeSpan> _sleep;

    public RuntimeGpuMonitorService(IEngineCounterProvider counters, ILuidAdapterResolver luidResolver,
        IProcessPathResolver pathResolver, RuntimeGpuMonitorOptions options = null, Action<TimeSpan> sleep = null)
    {
        _counters = counters;
        _luidResolver = luidResolver;
        _pathResolver = pathResolver;
        _options = options ?? new RuntimeGpuMonitorOptions();
        _sleep = sleep ?? (t => System.Threading.Thread.Sleep(t));
    }

    public RuntimeGpuResult Check(string targetExePath)
    {
        var windowStart = DateTimeOffset.UtcNow;
        targetExePath = targetExePath?.Trim();

        // Per-SAMPLE target matching: a pid's path is re-resolved every sample so PID
        // reuse (same pid, different exe) can never keep stale attribution.
        var targetPathByPid = new Dictionary<int, string>();  // pid -> path on samples where it matched
        var targetSeenIn = new Dictionary<int, int>();       // pid -> sample count where it matched
        var activity = new Dictionary<(int Pid, int Sample, string Luid), (double Total, bool Has3d)>();
        var engineTypesByPid = new Dictionary<int, HashSet<string>>();
        var activePidsOverall = new List<EngineSample>();
        var otherActiveSamples = new List<EngineSample>();   // non-target samples, for helper diagnostics
        bool providerFailed = false;
        string providerError = null;
        int sampleCount = 0;
        string normalizedTarget = Normalize(targetExePath);

        for (int s = 0; s < _options.WindowSamples; s++)
        {
            if (s > 0) _sleep(_options.PollInterval);
            var samples = new List<EngineSample>();
            if (!_counters.TryCollect(samples, out var err))
            {
                providerFailed = true;
                providerError = err;
                break;
            }
            sampleCount++;

            foreach (var sample in samples)
            {
                activePidsOverall.Add(sample);
                var path = SafePath(sample.Pid);
                bool isTarget = path != null &&
                    string.Equals(Normalize(path), normalizedTarget, StringComparison.OrdinalIgnoreCase);
                if (!isTarget)
                {
                    otherActiveSamples.Add(sample);
                    continue;
                }

                targetPathByPid[sample.Pid] = path;
                targetSeenIn[sample.Pid] = targetSeenIn.GetValueOrDefault(sample.Pid) + 1;

                if (!engineTypesByPid.TryGetValue(sample.Pid, out var types))
                    engineTypesByPid[sample.Pid] = types = new HashSet<string>();
                types.Add(sample.EngineType);

                var key = (sample.Pid, s, sample.Luid);
                var cur = activity.TryGetValue(key, out var v) ? v : (0.0, false);
                cur.Item1 = Math.Min(100.0, cur.Item1 + sample.UtilizationPercent);
                cur.Item2 |= sample.EngineType == "3d";
                activity[key] = cur;
            }

            // A target pid that exists but shows zero counter instances in a sample is
            // not observable here; the per-sample match counts below catch the gap.
        }

        var windowEnd = DateTimeOffset.UtcNow;
        var targetPids = targetSeenIn.Keys.ToList();

        // Helper/renderer diagnostics: busiest non-target processes (never attributed).
        string helperHint = BuildHelperHint(otherActiveSamples);

        // --- state machine ---

        if (providerFailed)
            return Unknown(_counters.SourceName, $"telemetry unavailable: {providerError}",
                windowStart, windowEnd, targetPids, engineTypesByPid, activity, Array.Empty<GpuAdapterInfo>(), activePidsOverall);

        if (string.IsNullOrWhiteSpace(targetExePath))
            return Unknown(_counters.SourceName, "no executable path given",
                windowStart, windowEnd, targetPids, engineTypesByPid, activity, Array.Empty<GpuAdapterInfo>(), activePidsOverall);

        if (targetPids.Count == 0)
        {
            return Unknown(_counters.SourceName,
                "no process with this executable path was observed during the sampling window" + helperHint,
                windowStart, windowEnd, targetPids, engineTypesByPid, activity, Array.Empty<GpuAdapterInfo>(), activePidsOverall);
        }

        // Every target pid must have matched in ALL samples; a pid appearing late,
        // vanishing mid-window, or flipping path (PID reuse) makes evidence partial.
        var partial = targetPids.Where(p => targetSeenIn[p] < sampleCount).ToList();

        // Peak per (pid,luid) with 3D flag; adapter resolution.
        var adaptersWith3d = new Dictionary<GpuAdapterInfo, List<int>>(); // adapter -> pids
        var adaptersAnyActivity = new Dictionary<GpuAdapterInfo, List<int>>();
        var adaptersWith3dBySample = new Dictionary<int, HashSet<GpuAdapterInfo>>();
        var unmappedLuids = new HashSet<string>();
        double peakAny = 0;
        var evidenceByPid = new Dictionary<int, (List<GpuAdapterInfo> Ads, double Peak, HashSet<string> Types)>();

        foreach (var pid in targetPids)
        {
            var ads = new List<GpuAdapterInfo>();
            double pidPeak = 0;
            foreach (var kv in activity)
            {
                if (kv.Key.Pid != pid) continue;
                var (luid, total, has3d) = (kv.Key.Luid, kv.Value.Total, kv.Value.Item2);
                bool active = total >= _options.ActivityThresholdPercent;
                if (!active) continue;
                pidPeak = Math.Max(pidPeak, total);
                var adapter = _luidResolver.Resolve(luid);
                if (adapter is null) { unmappedLuids.Add(luid); continue; }
                if (!ads.Contains(adapter)) ads.Add(adapter);
                if (has3d)
                {
                    if (!adaptersWith3d.TryGetValue(adapter, out var l)) adaptersWith3d[adapter] = l = new();
                    l.Add(pid);
                    if (!adaptersWith3dBySample.TryGetValue(kv.Key.Sample, out var sampleAdapters))
                        adaptersWith3dBySample[kv.Key.Sample] = sampleAdapters = new();
                    sampleAdapters.Add(adapter);
                }
                if (!adaptersAnyActivity.TryGetValue(adapter, out var l2)) adaptersAnyActivity[adapter] = l2 = new();
                l2.Add(pid);
            }
            peakAny = Math.Max(peakAny, pidPeak);
            evidenceByPid[pid] = (ads, pidPeak,
                engineTypesByPid.TryGetValue(pid, out var t) ? t : new HashSet<string>());
        }

        if (unmappedLuids.Count > 0 && adaptersWith3d.Count == 0)
            return Unknown(_counters.SourceName,
                $"activity seen on LUID(s) {string.Join(", ", unmappedLuids.OrderBy(x => x))} that did not map to any discovered adapter",
                windowStart, windowEnd, targetPids, engineTypesByPid, activity,
                adaptersAnyActivity.Keys.ToList(), activePidsOverall);

        bool any3d = adaptersWith3d.Count > 0;
        if (!any3d)
        {
            bool auxOnly = adaptersAnyActivity.Count > 0;
            if (partial.Count > 0)
                return Unknown(_counters.SourceName,
                    $"process(es) {string.Join(",", partial)} not present in every sample of the window",
                    windowStart, windowEnd, targetPids, engineTypesByPid, activity,
                    adaptersAnyActivity.Keys.ToList(), activePidsOverall);
            if (auxOnly)
                return Unknown(_counters.SourceName,
                    "only auxiliary engine activity (e.g. copy/video engines) was observed; rendering GPU cannot be established",
                    windowStart, windowEnd, targetPids, engineTypesByPid, activity,
                    adaptersAnyActivity.Keys.ToList(), activePidsOverall);
            return new RuntimeGpuResult(
                RuntimeGpuState.Inactive,
                $"matching process(es) observed in all {sampleCount} samples with valid telemetry, but no engine reached {_options.ActivityThresholdPercent}% — this is not proof the game never uses a GPU" + helperHint,
                Array.Empty<GpuAdapterInfo>(),
                BuildEvidence(evidenceByPid, targetPathByPid),
                engineTypesByPid.SelectMany(kv => kv.Value).Distinct().OrderBy(x => x).ToList(),
                windowStart, windowEnd - windowStart);
        }

        // 3D evidence exists — check for ambiguity.
        if (partial.Count > 0)
            return Unknown(_counters.SourceName,
                $"3D activity seen but process(es) {string.Join(",", partial)} were not present in every sample; evidence incomplete",
                windowStart, windowEnd, targetPids, engineTypesByPid, activity,
                adaptersWith3d.Keys.ToList(), activePidsOverall);

        var adapters = adaptersWith3d.Keys.ToList();
        if (adapters.Count > 1 && !adaptersWith3dBySample.Values.Any(sampleAdapters => sampleAdapters.Count > 1))
        {
            var names = string.Join(" + ", adapters.Select(a => a.DisplayName));
            return Unknown(_counters.SourceName,
                $"3D activity was observed on multiple adapters at different samples ({names}); simultaneous use could not be verified",
                windowStart, windowEnd, targetPids, engineTypesByPid, activity,
                Array.Empty<GpuAdapterInfo>(), activePidsOverall);
        }
        return new RuntimeGpuResult(
            adapters.Count > 1 ? RuntimeGpuState.MultipleAdapters : RuntimeGpuState.Verified,
            adapters.Count > 1
                ? $"3D rendering activity attributed to {adapters.Count} discovered adapters ({string.Join(" + ", adapters.Select(a => a.DisplayName))})"
                : $"3D rendering activity attributed to {adapters[0].DisplayName} [{adapters[0].AdapterId}]",
            adapters,
            BuildEvidence(evidenceByPid, targetPathByPid),
            engineTypesByPid.SelectMany(kv => kv.Value).Distinct().OrderBy(x => x).ToList(),
            windowStart, windowEnd - windowStart);
    }

    private List<ProcessGpuEvidence> BuildEvidence(
        Dictionary<int, (List<GpuAdapterInfo> Ads, double Peak, HashSet<string> Types)> byPid,
        Dictionary<int, string> matchedPids) =>
        byPid.Select(kv => new ProcessGpuEvidence(kv.Key,
            matchedPids.TryGetValue(kv.Key, out var p) ? p : null,
            kv.Value.Ads, kv.Value.Types.OrderBy(t => t).ToList(), Math.Round(kv.Value.Peak, 3)))
            .ToList();

    private string SafePath(int pid)
    {
        try { return _pathResolver.TryGetPath(pid); }
        catch { return null; }
    }

    /// <summary>Diagnostic text naming the busiest non-target processes, so a
    /// launcher→renderer handoff is visible without attributing it to the game.</summary>
    private string BuildHelperHint(List<EngineSample> otherSamples)
    {
        var other = otherSamples
            .GroupBy(a => a.Pid)
            .Select(g => (Pid: g.Key, Peak: g.Max(x => x.UtilizationPercent), Path: SafePath(g.Key)))
            .Where(x => x.Peak >= _options.ActivityThresholdPercent)
            .OrderByDescending(x => x.Peak).Take(3).ToList();
        if (other.Count == 0) return "";
        return " Other processes with GPU activity: " + string.Join(", ", other.Select(o =>
            $"{o.Pid}" + (o.Path != null ? $" ({System.IO.Path.GetFileName(o.Path)})" : " (path inaccessible)"))) +
            " — helper/child-process evidence is listed, not attributed to the target";
    }

    private RuntimeGpuResult Unknown(string source, string reason,
        DateTimeOffset start, DateTimeOffset end, List<int> targetPids,
        Dictionary<int, HashSet<string>> engineTypes,
        Dictionary<(int Pid, int Sample, string Luid), (double Total, bool Has3d)> activity,
        IReadOnlyList<GpuAdapterInfo> adapters, List<EngineSample> all) =>
        new(RuntimeGpuState.Unknown, reason, adapters,
            new List<ProcessGpuEvidence>(),
            engineTypes.Where(kv => targetPids.Contains(kv.Key)).SelectMany(kv => kv.Value)
                .Distinct().OrderBy(x => x).ToList(),
            start, end - start);

    private static string Normalize(string path) => path?.Replace('/', '\\').TrimEnd('\\').ToLowerInvariant();
}
