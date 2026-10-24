using System.Diagnostics;

internal sealed record Dx9TestTarget(string GameId, string ExecutablePath);

internal sealed class OscAdapterLifecycle : IDisposable
{
    private sealed record ActiveAdapter(int GameProcessId, string GameId, string DisplayName, Process Process);

    private readonly object _sync = new();
    private readonly string _buildRoot;
    private readonly string _executablePath;
    private readonly string _workingDirectory;
    private readonly int _stopGraceSeconds;
    private readonly Action<string> _log;
    private readonly Dictionary<int, ActiveAdapter> _active = new();
    private readonly bool _enabled;
    private readonly bool _allowUnverifiedIdentity;
    private readonly bool _enableDx9AutoHook;
    private readonly Dx9TestTarget[] _dx9TestTargets;
    private readonly bool _dx9HostContractReady;
    private string _lastError = "";

    internal OscAdapterLifecycle(
        string buildRoot,
        bool enabled,
        string executableRelativePath,
        string workingDirectoryRelativePath,
        int stopGraceSeconds,
        Action<string> log,
        bool allowUnverifiedIdentity = false,
        bool enableDx9AutoHook = false,
        IReadOnlyList<Dx9TestTarget> dx9TestTargets = null)
    {
        _buildRoot = Path.GetFullPath(buildRoot);
        _enabled = enabled;
        _allowUnverifiedIdentity = allowUnverifiedIdentity;
        _enableDx9AutoHook = enableDx9AutoHook;
        _dx9TestTargets = dx9TestTargets?.ToArray() ?? Array.Empty<Dx9TestTarget>();
        _log = log;
        _stopGraceSeconds = Math.Clamp(stopGraceSeconds, 0, 15);

        _executablePath = ResolveInsideBuildRoot(executableRelativePath);
        _workingDirectory = ResolveInsideBuildRoot(
            string.IsNullOrWhiteSpace(workingDirectoryRelativePath)
                ? Path.GetDirectoryName(executableRelativePath)
                : workingDirectoryRelativePath);

        if (_enabled && string.IsNullOrWhiteSpace(_executablePath))
            _lastError = "OSC Adapter is enabled but no relative executable path is configured.";
        else if (_enabled && !File.Exists(_executablePath))
            _lastError = "Configured OSC Adapter executable does not exist in Build.";

        // A legacy runtime Host silently ignores new CLI switches. Do not
        // emit --render-api until BOTH x64 and x86 deployed hosts support it.
        _dx9HostContractReady = HasDx9HostContract(_executablePath);
    }

    internal void StartForGame(GameMatch match)
    {
        if (match is null) return;
        lock (_sync)
        {
            if (!_enabled) return;

            // Ontology is only an allowlist/hint, never runtime renderer proof.
            // The Host inspects the game's loaded D3D modules; native Hook
            // determines whether the actual D3D9 presentation device is Ex.
            var requestedApi = SelectRenderApi(match.RenderApis);
            if (requestedApi is null)
            {
                _log("[osc-adapter] skipped " + match.GameId +
                    ": unsupported/ambiguous catalog renderer; no attach.");
                return;
            }
            var isDx9 = requestedApi == "d3d9";
            if (isDx9 && !_enableDx9AutoHook)
            {
                _log("[osc-adapter] DX9 auto-hook gated off until explicit rollout approval.");
                return;
            }
            if (isDx9 && !IsAllowedDx9TestTarget(match.GameId, match.ExecutablePath))
            {
                _log("[osc-adapter] DX9 denied: game ID/executable path not in exact test allowlist.");
                return;
            }
            if (isDx9 && !_dx9HostContractReady)
            {
                _lastError = "DX9 host contract unavailable in deployed x64/x86 Adapter; no attach.";
                _log("[osc-adapter] " + _lastError);
                return;
            }
            // During controlled development, the same explicit identity override
            // may permit name-only matching for DX9 or D3D11. It never permits
            // unknown/unmatched identities; release default remains SHA-256.
            if (!IsIdentityEligible(match.MatchMethod, _allowUnverifiedIdentity))
            {
                _log("[osc-adapter] skipped " + match.GameId +
                    ": verified game SHA-256 identity required for this route.");
                return;
            }
            if (match.MatchMethod != "sha256")
                _log("[osc-adapter] WARNING: development identity override accepted unverified game " + match.GameId + " route=" + requestedApi);

            // Recheck the actual PID, process start time and Windows-reported
            // executable path at launch time; reject stale/PID-recycled matches.
            if (!IsCurrentProcessIdentity(match))
            {
                _log("[osc-adapter] denied: PID/start-time/executable-path mismatch.");
                return;
            }
            if (_active.TryGetValue(match.ProcessId, out var current))
            {
                if (!current.Process.HasExited &&
                    string.Equals(current.GameId, match.GameId, StringComparison.Ordinal))
                    return;
                StopLocked(match.ProcessId);
            }

            if (string.IsNullOrWhiteSpace(_executablePath) ||
                !IsInsideBuildRoot(_executablePath) ||
                !File.Exists(_executablePath))
            {
                SetMissingExecutableErrorLocked();
                return;
            }

            try
            {
                var workingDirectory = !string.IsNullOrWhiteSpace(_workingDirectory) &&
                    Directory.Exists(_workingDirectory)
                    ? _workingDirectory
                    : Path.GetDirectoryName(_executablePath);

                var startInfo = new ProcessStartInfo
                {
                    FileName = _executablePath,
                    WorkingDirectory = workingDirectory,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                startInfo.ArgumentList.Add("--game-id");
                startInfo.ArgumentList.Add(match.GameId);
                startInfo.ArgumentList.Add("--game-name");
                startInfo.ArgumentList.Add(match.DisplayName);
                startInfo.ArgumentList.Add("--game-pid");
                startInfo.ArgumentList.Add(match.ProcessId.ToString());
                startInfo.ArgumentList.Add("--game-executable");
                startInfo.ArgumentList.Add(match.ExecutablePath);
                // Old deployed Host has no --render-api. Leave D3D11 CLI
                // unchanged for backwards compatibility.
                if (isDx9)
                {
                    startInfo.ArgumentList.Add("--render-api");
                    startInfo.ArgumentList.Add("d3d9"); // never guess d3d9ex from catalog variant
                }

                var process = Process.Start(startInfo);
                if (process is null)
                {
                    _lastError = "Process.Start returned no OSC Adapter process.";
                    _log("[osc-adapter] " + _lastError);
                    return;
                }

                _active[match.ProcessId] = new ActiveAdapter(
                    match.ProcessId, match.GameId, match.DisplayName, process);
                _lastError = "";
                _log("[osc-adapter] started PID " + process.Id + " for " +
                    match.DisplayName + " (game PID " + match.ProcessId +
                    ", route=" + requestedApi + ", runtime evidence pending)");
            }
            catch (Exception ex)
            {
                _lastError = ex.GetType().Name + ": " + ex.Message;
                _log("[osc-adapter] start failed for game PID " + match.ProcessId + ": " + _lastError);
            }
        }
    }

    internal bool IsAllowedDx9TestTarget(string gameId, string executablePath)
    {
        if (!_enableDx9AutoHook || string.IsNullOrWhiteSpace(gameId) ||
            string.IsNullOrWhiteSpace(executablePath)) return false;
        try
        {
            var actual = Path.GetFullPath(executablePath);
            return _dx9TestTargets.Any(item =>
                string.Equals(item.GameId, gameId, StringComparison.Ordinal) &&
                Path.IsPathFullyQualified(item.ExecutablePath) &&
                string.Equals(Path.GetFullPath(item.ExecutablePath), actual,
                    StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
        {
            return false;
        }
    }

    internal static bool IsCurrentProcessIdentity(GameMatch match)
    {
        if (match is null || match.ProcessId <= 0 ||
            string.IsNullOrWhiteSpace(match.ProcessSignature)) return false;
        try
        {
            using var process = Process.GetProcessById(match.ProcessId);
            if (process.HasExited) return false;
            var livePath = process.MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(livePath) ||
                !Path.IsPathFullyQualified(livePath)) return false;
            var full = Path.GetFullPath(livePath);
            var started = process.StartTime.ToUniversalTime();
            var signature = process.Id + "|" + started.Ticks + "|" + full;
            return string.Equals(full, Path.GetFullPath(match.ExecutablePath),
                    StringComparison.OrdinalIgnoreCase) &&
                string.Equals(Path.GetFileName(full), match.ExecutableName,
                    StringComparison.OrdinalIgnoreCase) &&
                string.Equals(signature, match.ProcessSignature, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    // Catalog d3d9ex variants are advisory: the native Hook must observe
    // IDirect3D9Ex / PresentEx; Container only requests d3d9 family.
    internal static string SelectRenderApi(IReadOnlyList<string> renderApis) =>
        renderApis is { Count: 1 } &&
        string.Equals(renderApis[0], "d3d11", StringComparison.OrdinalIgnoreCase)
            ? "d3d11"
            : renderApis is { Count: 1 } &&
              string.Equals(renderApis[0], "d3d9", StringComparison.OrdinalIgnoreCase)
                ? "d3d9" : null;

    // Preserve existing DX11-only helper for legacy regression/contracts.
    internal static bool SupportsCurrentAdapter(IReadOnlyList<string> renderApis) =>
        SelectRenderApi(renderApis) == "d3d11";

    // This is a deployment compatibility check, NOT code-signing, identity,
    // anti-cheat clearance or proof of real GPU composition.
    internal static bool HasDx9HostContract(string hostExePath)
    {
        if (string.IsNullOrWhiteSpace(hostExePath) || !File.Exists(hostExePath))
            return false;
        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(hostExePath));
            if (directory is null) return false;
            var x86 = Path.Combine(directory, "x86");
            return PeMachine(Path.Combine(directory, "NVIDIA OSC Adapter.exe")) == 0x8664 &&
                PeMachine(Path.Combine(x86, "NVIDIA OSC Adapter.exe")) == 0x014c &&
                PeMachine(Path.Combine(directory, "NVIDIA OSC Adapter Hook.dll")) == 0x8664 &&
                PeMachine(Path.Combine(x86, "NVIDIA OSC Adapter Hook.dll")) == 0x014c &&
                ManagedHostSupportsApi(Path.Combine(directory, "NVIDIA OSC Adapter.dll")) &&
                ManagedHostSupportsApi(Path.Combine(x86, "NVIDIA OSC Adapter.dll"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool ManagedHostSupportsApi(string managedPath)
    {
        if (!File.Exists(managedPath)) return false;
        var info = new FileInfo(managedPath);
        if (info.Length < 1024 || info.Length > 8 * 1024 * 1024) return false;
        var bytes = File.ReadAllBytes(managedPath);
        return System.Text.Encoding.UTF8.GetString(bytes).Contains("--render-api", StringComparison.Ordinal);
    }

    private static ushort PeMachine(string path)
    {
        if (!File.Exists(path)) return 0;
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        if (file.Length < 128) return 0;
        using var reader = new BinaryReader(file);
        if (reader.ReadUInt16() != 0x5A4D) return 0;
        file.Position = 0x3C;
        var pe = reader.ReadInt32();
        if (pe < 0 || pe > file.Length - 6) return 0;
        file.Position = pe;
        if (reader.ReadUInt32() != 0x00004550) return 0;
        return reader.ReadUInt16();
    }

    // Discovery by executable name is not authentication: fail closed for injected hooks.
    internal static bool IsIdentityEligible(string matchMethod, bool allowUnverifiedIdentity) =>
        string.Equals(matchMethod, "sha256", StringComparison.Ordinal) ||
        (allowUnverifiedIdentity && string.Equals(matchMethod, "executableName", StringComparison.Ordinal));

    internal void StopForGameProcess(int gameProcessId, string reason)
    {
        lock (_sync)
        {
            if (!_active.ContainsKey(gameProcessId)) return;
            _log("[osc-adapter] stopping game PID " + gameProcessId + ": " + reason);
            StopLocked(gameProcessId);
        }
    }

    internal void StopAll(string reason)
    {
        lock (_sync)
        {
            if (_active.Count == 0) return;
            _log("[osc-adapter] reconciling " + _active.Count + " instance(s): " + reason);
            foreach (var (id, active) in _active.ToArray())
            {
                if (!active.Process.HasExited)
                {
                    _log("[osc-adapter] retaining host PID " + SafeProcessId(active.Process) +
                        " for game PID " + id +
                        " until the game exits; unloading an injected module is not supported yet.");
                    continue;
                }
                StopLocked(id);
            }
        }
    }

    private void StopLocked(int gameProcessId)
    {
        if (!_active.Remove(gameProcessId, out var active)) return;
        using (active.Process)
        {
            try
            {
                if (active.Process.HasExited) return;
                active.Process.CloseMainWindow();
                if (!active.Process.WaitForExit(_stopGraceSeconds * 1000))
                {
                    // Only terminate the adapter host process that this manager launched.
                    active.Process.Kill(entireProcessTree: true);
                    active.Process.WaitForExit(2000);
                }
            }
            catch (Exception ex)
            {
                _lastError = ex.GetType().Name + ": " + ex.Message;
                _log("[osc-adapter] stop failed for PID " + active.Process.Id + ": " + ex.Message);
            }
        }
    }

    internal object GetStatus()
    {
        lock (_sync)
        {
            var running = _active.Values
                .Where(item => !item.Process.HasExited)
                .Select(item => new
                {
                    gameProcessId = item.GameProcessId,
                    gameId = item.GameId,
                    displayName = item.DisplayName,
                    adapterHostProcessId = SafeProcessId(item.Process)
                })
                .ToArray();

            return new
            {
                enabled = _enabled,
                requireVerifiedIdentity = !_allowUnverifiedIdentity,
                dx9RequiresVerifiedIdentity = !_allowUnverifiedIdentity,
                dx9AutoHookEnabled = _enableDx9AutoHook,
                dx9HostContractReady = _dx9HostContractReady,
                dx9TestTargetsConfigured = _dx9TestTargets.Length,
                dx9AutoHookEligible = _enabled && _enableDx9AutoHook &&
                    _dx9TestTargets.Length > 0 && _dx9HostContractReady,
                dx9ExVerified = false, // actual Ex is only detected inside native Hook
                adapter = "osc",
                architecture = "x64",
                dxgiPresentHookImplemented = true,
                cefTextureBridgeImplemented = true,
                overlayCompositionImplemented = true,
                configuredExecutable = _executablePath,
                executableExists = !string.IsNullOrWhiteSpace(_executablePath) && File.Exists(_executablePath),
                activeInstances = running,
                activeCount = running.Length,
                lastError = _lastError
            };
        }
    }

    private void SetMissingExecutableErrorLocked()
    {
        const string message = "Configured OSC Adapter executable does not exist in Build.";
        if (string.Equals(_lastError, message, StringComparison.Ordinal)) return;
        _lastError = message;
        _log("[osc-adapter] " + message + " Path=" + _executablePath);
    }

    private string ResolveInsideBuildRoot(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
            return "";

        var fullPath = Path.GetFullPath(Path.Combine(_buildRoot, relativePath));
        return IsInsideBuildRoot(fullPath) ? fullPath : "";
    }

    private bool IsInsideBuildRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var root = _buildRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(path);
        return fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    private static int SafeProcessId(Process process)
    {
        try { return process.Id; }
        catch { return 0; }
    }

    public void Dispose() => StopAll("NvContainer stopped");
}
