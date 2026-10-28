using System;
using System.Collections.Generic;

// Pure state shared with the isolated regression runner; no Win32 registration.
internal sealed class HotkeyMessageGate
{
    private readonly Dictionary<string, (uint Key, uint Time)> _last = new(StringComparer.Ordinal);

    internal bool TryAccept(string name, uint key, uint messageTime)
    {
        if (_last.TryGetValue(name, out var previous) && previous == (key, messageTime)) return false;
        _last[name] = (key, messageTime);
        return true;
    }

    internal void Clear() => _last.Clear();
}

internal sealed class HotkeyDispatchRequest
{
    internal readonly string Url, Body, RequestId;
    internal readonly long QueuedAt, CreatedAtUnixMs, Generation;

    internal HotkeyDispatchRequest(string url, string body, long queuedAt,
        long createdAtUnixMs, long generation)
    {
        Url = url;
        Body = body;
        QueuedAt = queuedAt;
        CreatedAtUnixMs = createdAtUnixMs;
        Generation = generation;
        RequestId = Guid.NewGuid().ToString("N");
    }

    internal bool CanDispatch(long now, long generation, bool monitoringEnabled) =>
        monitoringEnabled && Generation == generation && now - QueuedAt <= 2000;
}
