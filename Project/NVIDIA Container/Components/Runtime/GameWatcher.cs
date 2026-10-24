using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

internal sealed record GameMatch(
    int ProcessId,
    string ProcessSignature,
    string ExecutablePath,
    string ExecutableName,
    string GameId,
    string DisplayName,
    string CatalogVersion,
    DateTime MatchedUtc,
    IReadOnlyList<string> RenderApis,
    string MatchMethod);

internal sealed class GameWatcher : IDisposable
{
    private const uint ProcessQueryLimitedInformation = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(
        uint desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode,
        EntryPoint = "QueryFullProcessImageNameW", SetLastError = true)]
    private static extern bool QueryFullProcessImageName(
        SafeProcessHandle process, uint flags, StringBuilder executablePath,
        ref uint size);

    private sealed record ObservedProcess(
        int ProcessId,
        string Signature,
        string ExecutablePath,
        string ExecutableName,
        DateTime StartedUtc);

    private sealed record CachedResolution(
        bool Matched,
        string GameId,
        string DisplayName,
        string Reason,
        string CatalogVersion,
        IReadOnlyList<string> RenderApis,
        string MatchMethod);

    private readonly object _sync = new();
    private readonly ApplicationOntologyClient _ontology;
    private readonly OscAdapterLifecycle _oscAdapter;
    private readonly Action<object> _publishStatus;
    private readonly Action<string> _log;
    private readonly bool _enabled;
    private readonly int _pollIntervalSeconds;
    private readonly int _catalogRefreshSeconds;
    private readonly CancellationTokenSource _cancellation = new();

    // Loop-owned state (only touched by RunLoopAsync).
    private readonly Dictionary<string, CachedResolution> _resolutionCache =
        new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _catalogExecutableNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, GameMatch> _matchedProcesses = new();
    private string _catalogVersion = "";
    private string _catalogIdentityFingerprint = "";
    private int _catalogGameCount;
    private bool _catalogReady;
    private bool _catalogReachable;
    private string _catalogError = "";
    private int _candidateProcessCount;
    private int _lastRefreshStatusCode;
    private DateTime? _lastCatalogRefreshUtc;
    private DateTime? _lastScanUtc;
    private Task _runTask;
    private bool _disposed;

    // Only status fields are published to the local diagnostics API.
    private bool _running;
    private string _lastError = "";
    private bool _publishedCatalogReady;
    private bool _publishedCatalogReachable;
    private bool _publishedCatalogEmpty;
    private string _publishedCatalogSource = "unavailable";
    private string _publishedCatalogVersion = "";
    private int _publishedCatalogGameCount;
    private int _publishedKnownExecutableNames;
    private int _publishedResolutionCacheCount;
    private int _publishedCandidateProcessCount;
    private DateTime? _publishedLastCatalogRefreshUtc;
    private DateTime? _publishedLastScanUtc;
    private int _publishedLastRefreshStatusCode;
    private string _publishedCatalogError = "";
    private string _publishedLastError = "";
    private GameMatch[] _publishedMatches = Array.Empty<GameMatch>();
    private string _lastPublishedFingerprint = "";
    private DateTime _lastStatusPublishedUtc = DateTime.MinValue;

    internal GameWatcher(
        ApplicationOntologyClient ontology,
        OscAdapterLifecycle oscAdapter,
        bool enabled,
        int pollIntervalSeconds,
        int catalogRefreshSeconds,
        Action<object> publishStatus,
        Action<string> log)
    {
        _ontology = ontology;
        _oscAdapter = oscAdapter;
        _publishStatus = publishStatus;
        _enabled = enabled;
        _pollIntervalSeconds = Math.Clamp(pollIntervalSeconds, 1, 15);
        _catalogRefreshSeconds = Math.Clamp(catalogRefreshSeconds, 10, 300);
        _log = log;
    }

    internal void Start()
    {
        lock (_sync)
        {
            if (_runTask != null || _disposed) return;
            if (!_enabled)
            {
                _running = false;
                _lastError = "Game watcher is disabled by configuration.";
                _log("[game-watcher] disabled by configuration");
                return;
            }

            _running = true;
            _runTask = Task.Run(() => RunLoopAsync(_cancellation.Token));
        }
        _log("[game-watcher] started; poll=" + _pollIntervalSeconds +
            "s catalogRefresh=" + _catalogRefreshSeconds +
            "s resolution=local");
    }

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        var nextCatalogRefresh = DateTime.MinValue;
        var previousVersion = "";
        var previousNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ApplyCatalogSnapshot(_ontology.LoadCachedCatalog(),
            ref previousVersion, ref previousNames, isRefresh: false);
        Task<OntologyCatalogSnapshot> refreshTask = null;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (refreshTask != null && refreshTask.IsCompleted)
                {
                    var snapshot = await refreshTask.ConfigureAwait(false);
                    refreshTask = null;
                    _lastCatalogRefreshUtc = DateTime.UtcNow;
                    _lastRefreshStatusCode = snapshot.StatusCode;
                    ApplyCatalogSnapshot(snapshot,
                        ref previousVersion, ref previousNames, isRefresh: true);
                }

                if (refreshTask == null && DateTime.UtcNow >= nextCatalogRefresh)
                {
                    refreshTask = _ontology.GetExecutableCatalogAsync(cancellationToken);
                    nextCatalogRefresh = DateTime.UtcNow.AddSeconds(_catalogRefreshSeconds);
                    _log("[game-watcher] started online catalog refresh; local catalog remains active");
                }

                var candidates = ScanCatalogCandidateProcesses(_catalogExecutableNames);
                _candidateProcessCount = candidates.Count;
                _lastScanUtc = DateTime.UtcNow;

                // Always reap processes which exited or whose PID was reused, even if
                // the public service is temporarily unavailable.
                foreach (var active in _matchedProcesses.ToArray())
                {
                    if (!candidates.TryGetValue(active.Key, out var process) ||
                        !string.Equals(process.Signature, active.Value.ProcessSignature, StringComparison.Ordinal))
                    {
                        _oscAdapter.StopForGameProcess(active.Key, "game process exited or PID was reused");
                        _matchedProcesses.Remove(active.Key);
                    }
                }

                if (_enabled && _catalogReady && _catalogGameCount > 0 && _catalogExecutableNames.Count > 0)
                {
                    // Pin-based identity verification must be per process image,
                    // never cached under a shared executable basename.
                    var liveSignatures = candidates.Values.Select(x => x.Signature)
                        .ToHashSet(StringComparer.Ordinal);
                    foreach (var stale in _resolutionCache.Keys.Where(x => !liveSignatures.Contains(x)).ToArray())
                        _resolutionCache.Remove(stale);
                    var toResolve = candidates.Values
                        .Where(candidate => !_resolutionCache.ContainsKey(candidate.Signature))
                        .ToArray();

                    if (toResolve.Length > 0)
                        await ResolveCandidatesAsync(toResolve, cancellationToken).ConfigureAwait(false);

                    foreach (var candidate in candidates.Values)
                    {
                        if (!_resolutionCache.TryGetValue(candidate.Signature, out var resolution) ||
                            !resolution.Matched)
                            continue;

                        if (_matchedProcesses.TryGetValue(candidate.ProcessId, out var existing) &&
                            string.Equals(existing.ProcessSignature, candidate.Signature, StringComparison.Ordinal))
                            continue;

                        var match = new GameMatch(
                            candidate.ProcessId,
                            candidate.Signature,
                            candidate.ExecutablePath,
                            candidate.ExecutableName,
                            resolution.GameId,
                            resolution.DisplayName,
                            resolution.CatalogVersion,
                            DateTime.UtcNow,
                            resolution.RenderApis,
                            resolution.MatchMethod);
                        _matchedProcesses[candidate.ProcessId] = match;
                        _log("[game-watcher] matched " + match.DisplayName + " (" + match.GameId +
                            ") PID=" + match.ProcessId + " via " + match.MatchMethod +
                            " renderApis=" + string.Join(",", match.RenderApis));
                        _oscAdapter.StartForGame(match);
                    }
                }

                PublishStatus();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _lastError = ex.GetType().Name + ": " + ex.Message;
                _log("[game-watcher] scan cycle failed: " + _lastError);
                PublishStatus();
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(_pollIntervalSeconds), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
        }

        _oscAdapter.StopAll("game watcher stopped");
        lock (_sync) _running = false;
    }

    private void ApplyCatalogSnapshot(OntologyCatalogSnapshot snapshot,
        ref string previousVersion, ref HashSet<string> previousNames,
        bool isRefresh)
    {
        if (snapshot.Ready)
        {
            // The same catalog version may contain corrected hashes or render
            // APIs. Invalidate previous identity decisions on any such change.
            var identityFingerprint = JsonSerializer.Serialize(snapshot.Games
                .OrderBy(x => x.GameId, StringComparer.Ordinal)
                .Select(x => new {
                    x.GameId, x.DisplayName,
                    Names = x.ExecutableNames.OrderBy(v => v, StringComparer.OrdinalIgnoreCase),
                    Apis = x.RenderApis.OrderBy(v => v, StringComparer.Ordinal),
                    Hashes = x.Sha256.OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
                }));
            var catalogChanged =
                !string.Equals(previousVersion, snapshot.CatalogVersion, StringComparison.Ordinal) ||
                !previousNames.SetEquals(snapshot.ExecutableNames) ||
                !string.Equals(_catalogIdentityFingerprint, identityFingerprint, StringComparison.Ordinal);
            if (catalogChanged)
            {
                if (previousVersion.Length > 0)
                {
                    _oscAdapter.StopAll("ApplicationOntology catalog changed");
                    _matchedProcesses.Clear();
                    _resolutionCache.Clear();
                    _log("[game-watcher] catalog changed; cleared cached game identity decisions");
                }
                previousVersion = snapshot.CatalogVersion;
                _catalogIdentityFingerprint = identityFingerprint;
                previousNames = new HashSet<string>(
                    snapshot.ExecutableNames, StringComparer.OrdinalIgnoreCase);
            }

            _catalogVersion = snapshot.CatalogVersion;
            _catalogGameCount = snapshot.GameCount;
            _catalogExecutableNames = new HashSet<string>(
                snapshot.ExecutableNames, StringComparer.OrdinalIgnoreCase);
            _catalogReady = true;
            _catalogReachable = snapshot.Reachable;
            _catalogError = snapshot.Error ?? "";
            _lastError = snapshot.GameCount == 0
                ? "ApplicationOntology catalog is empty."
                : "";
        }
        else if (isRefresh)
        {
            _catalogReachable = snapshot.Reachable;
            _catalogError = snapshot.Error ?? "ApplicationOntology is unavailable.";
            if (!_catalogReady)
                _lastError = _catalogError;
            _log("[game-watcher] online catalog refresh failed: HTTP " +
                snapshot.StatusCode + " " + _catalogError +
                (_catalogReady ? "; continuing with local cache." : ""));
        }
        else
        {
            _catalogReady = false;
            _catalogReachable = false;
            _catalogError = snapshot.Error ?? "Local ApplicationOntology catalog is unavailable.";
            _lastError = _catalogError;
            _log("[game-watcher] local catalog unavailable: " + _catalogError);
        }
        PublishStatus();
    }

    private Dictionary<int, ObservedProcess> ScanCatalogCandidateProcesses(IReadOnlySet<string> catalogNames)
    {
        var found = new Dictionary<int, ObservedProcess>();
        if (catalogNames.Count == 0) return found;

        int sessionId;
        try { sessionId = Process.GetCurrentProcess().SessionId; }
        catch { sessionId = -1; }

        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.Id == Environment.ProcessId || process.SessionId != sessionId)
                        continue;

                    if (!TryGetExecutablePath(process.Id, out var path) ||
                        !Path.IsPathFullyQualified(path))
                        continue;

                    var executableName = Path.GetFileName(path);
                    if (string.IsNullOrWhiteSpace(executableName) ||
                        !catalogNames.Contains(executableName) ||
                        !string.Equals(Path.GetExtension(executableName), ".exe", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var fullPath = Path.GetFullPath(path);
                    var startedUtc = process.StartTime.ToUniversalTime();
                    var signature = process.Id + "|" + startedUtc.Ticks + "|" + fullPath;
                    found[process.Id] = new ObservedProcess(
                        process.Id, signature, fullPath, executableName, startedUtc);
                }
                catch
                {
                    // Protected and exiting processes are intentionally skipped.
                }
            }
        }

        return found;
    }

    private static bool TryGetExecutablePath(int processId, out string executablePath)
    {
        executablePath = "";
        using var process = OpenProcess(
            ProcessQueryLimitedInformation, false, processId);
        if (process.IsInvalid)
            return false;

        var buffer = new StringBuilder(32768);
        uint length = (uint)buffer.Capacity;
        if (!QueryFullProcessImageName(process, 0, buffer, ref length))
            return false;

        executablePath = buffer.ToString();
        return !string.IsNullOrWhiteSpace(executablePath);
    }

    private async Task ResolveCandidatesAsync(IReadOnlyList<ObservedProcess> candidates,
        CancellationToken cancellationToken)
    {
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var resolution = _ontology.ResolveExecutable(candidate.ExecutableName);
            if (resolution == null)
            {
                _lastError = "Local game catalog is unavailable.";
                continue;
            }

            if (!resolution.Matched && resolution.Reason == "hash_required")
            {
                // Only hash allowlisted processes with a pinned SHA-256. No
                // hashing every scan; this signature is cached for its lifetime.
                // Never fall back to basename if the hash fails to verify.
                try
                {
                    var info = new FileInfo(candidate.ExecutablePath);
                    if (info.Length > 0 && info.Length <= 512L * 1024 * 1024)
                    {
                        await using var stream = new FileStream(candidate.ExecutablePath,
                            FileMode.Open, FileAccess.Read,
                            FileShare.ReadWrite | FileShare.Delete, 81920,
                            FileOptions.Asynchronous | FileOptions.SequentialScan);
                        var digest = await SHA256.HashDataAsync(stream, cancellationToken)
                            .ConfigureAwait(false);
                        resolution = _ontology.ResolveExecutable(candidate.ExecutableName,
                            Convert.ToHexString(digest));
                    }
                    else
                        _log("[game-watcher] pinned image skipped: size invalid or exceeds 512 MiB");
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                {
                    _log("[game-watcher] pinned image could not be verified: " + ex.GetType().Name);
                }
            }

            _resolutionCache[candidate.Signature] = new CachedResolution(
                resolution.Matched, resolution.GameId, resolution.DisplayName,
                resolution.Reason, resolution.CatalogVersion,
                resolution.RenderApis, resolution.MatchMethod);
            if (!resolution.Matched && resolution.Reason != "not_found")
                _log("[game-watcher] skipped unverifiable identity " + candidate.ExecutableName +
                    " reason=" + resolution.Reason);
        }
    }

    private void PublishStatus()
    {
        string fingerprint;
        lock (_sync)
        {
            _publishedCatalogReady = _catalogReady;
            _publishedCatalogReachable = _catalogReachable;
            _publishedCatalogEmpty = _catalogReady && _catalogGameCount == 0;
            _publishedCatalogSource = _catalogReady ? "local-cache" : "unavailable";
            _publishedCatalogVersion = _catalogVersion;
            _publishedCatalogGameCount = _catalogGameCount;
            _publishedKnownExecutableNames = _catalogExecutableNames.Count;
            _publishedResolutionCacheCount = _resolutionCache.Count;
            _publishedCandidateProcessCount = _candidateProcessCount;
            _publishedLastCatalogRefreshUtc = _lastCatalogRefreshUtc;
            _publishedLastScanUtc = _lastScanUtc;
            _publishedLastRefreshStatusCode = _lastRefreshStatusCode;
            _publishedCatalogError = _catalogError;
            _publishedLastError = _lastError;
            _publishedMatches = _matchedProcesses.Values
                .OrderBy(match => match.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            // Ignore scan timestamps when comparing state, otherwise every poll
            // would generate an event even if nothing actually changed.
            fingerprint = JsonSerializer.Serialize(new
            {
                enabled = _enabled,
                running = _running,
                catalogReady = _publishedCatalogReady,
                catalogSource = _publishedCatalogSource,
                catalogReachable = _publishedCatalogReachable,
                catalogEmpty = _publishedCatalogEmpty,
                catalogVersion = _publishedCatalogVersion,
                catalogGameCount = _publishedCatalogGameCount,
                knownExecutableNames = _publishedKnownExecutableNames,
                cachedExecutableResolutions = _publishedResolutionCacheCount,
                candidateProcessCount = _publishedCandidateProcessCount,
                matchedGames = _publishedMatches.Select(match => new
                {
                    processId = match.ProcessId,
                    executableName = match.ExecutableName,
                    executablePath = match.ExecutablePath,
                    gameId = match.GameId,
                    displayName = match.DisplayName,
                    catalogVersion = match.CatalogVersion,
                    matchedUtc = match.MatchedUtc,
                    renderApis = match.RenderApis,
                    matchMethod = match.MatchMethod,
                    identityVerified = match.MatchMethod == "sha256"
                }).ToArray(),
                catalogError = _publishedCatalogError,
                lastError = _publishedLastError
            });
        }

        if (_publishStatus == null) return;
        var now = DateTime.UtcNow;
        if (string.Equals(fingerprint, _lastPublishedFingerprint, StringComparison.Ordinal) &&
            now - _lastStatusPublishedUtc < TimeSpan.FromSeconds(15))
            return;

        try
        {
            _publishStatus(new
            {
                schemaVersion = 1,
                eventId = Guid.NewGuid().ToString("N"),
                publishedUtc = now.ToString("O"),
                status = GetStatus()
            });
            _lastPublishedFingerprint = fingerprint;
            _lastStatusPublishedUtc = now;
        }
        catch (Exception ex)
        {
            _log("[game-watcher] status event publish failed: " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    internal object GetStatus()
    {
        lock (_sync)
        {
            return new
            {
                enabled = _enabled,
                running = _running,
                catalogReady = _publishedCatalogReady,
                catalogSource = _publishedCatalogSource,
                catalogReachable = _publishedCatalogReachable,
                catalogEmpty = _publishedCatalogEmpty,
                catalogVersion = _publishedCatalogVersion,
                catalogGameCount = _publishedCatalogGameCount,
                knownExecutableNames = _publishedKnownExecutableNames,
                cachedExecutableResolutions = _publishedResolutionCacheCount,
                candidateProcessCount = _publishedCandidateProcessCount,
                matchedGameCount = _publishedMatches.Length,
                matchedGames = _publishedMatches.Select(match => new
                {
                    processId = match.ProcessId,
                    executableName = match.ExecutableName,
                    executablePath = match.ExecutablePath,
                    gameId = match.GameId,
                    displayName = match.DisplayName,
                    catalogVersion = match.CatalogVersion,
                    matchedUtc = match.MatchedUtc,
                    renderApis = match.RenderApis,
                    matchMethod = match.MatchMethod,
                    identityVerified = match.MatchMethod == "sha256"
                }).ToArray(),
                lastCatalogRefreshUtc = _publishedLastCatalogRefreshUtc,
                lastScanUtc = _publishedLastScanUtc,
                lastRefreshStatusCode = _publishedLastRefreshStatusCode,
                catalogError = _publishedCatalogError,
                lastError = _publishedLastError
            };
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
        }
        _cancellation.Cancel();
        _oscAdapter.StopAll("NvContainer disposed");
        _cancellation.Dispose();
    }
}
