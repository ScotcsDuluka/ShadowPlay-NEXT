using System.Diagnostics;

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
    private string _lastError = "";

    internal OscAdapterLifecycle(
        string buildRoot,
        bool enabled,
        string executableRelativePath,
        string workingDirectoryRelativePath,
        int stopGraceSeconds,
        Action<string> log)
    {
        _buildRoot = Path.GetFullPath(buildRoot);
        _enabled = enabled;
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
    }

    internal void StartForGame(GameMatch match)
    {
        if (match is null) return;
        lock (_sync)
        {
            if (!_enabled) return;

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
                    match.DisplayName + " (game PID " + match.ProcessId + ")");
            }
            catch (Exception ex)
            {
                _lastError = ex.GetType().Name + ": " + ex.Message;
                _log("[osc-adapter] start failed for game PID " + match.ProcessId + ": " + _lastError);
            }
        }
    }

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
