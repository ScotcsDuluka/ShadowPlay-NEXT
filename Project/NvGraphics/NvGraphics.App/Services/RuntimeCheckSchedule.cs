namespace NvGraphics.Services;

/// <summary>Throttles runtime checks per executable while allowing checks to repeat
/// during one process lifetime.</summary>
public sealed class RuntimeCheckSchedule
{
    private readonly TimeSpan _interval;
    private readonly Dictionary<string, DateTimeOffset> _lastChecks =
        new(StringComparer.OrdinalIgnoreCase);

    public RuntimeCheckSchedule(TimeSpan? interval = null)
    {
        _interval = interval ?? TimeSpan.FromSeconds(30);
        if (_interval < TimeSpan.FromMilliseconds(500))
            throw new ArgumentOutOfRangeException(nameof(interval), "Runtime checks must be at least 500 ms apart.");
    }

    public bool ShouldCheck(string exePath, DateTimeOffset now, bool processRunning)
    {
        if (string.IsNullOrWhiteSpace(exePath))
            throw new ArgumentException("Executable path is required.", nameof(exePath));

        if (!processRunning)
        {
            _lastChecks.Remove(exePath);
            return false;
        }

        if (_lastChecks.TryGetValue(exePath, out var last) && now - last < _interval)
            return false;

        _lastChecks[exePath] = now;
        return true;
    }
}
