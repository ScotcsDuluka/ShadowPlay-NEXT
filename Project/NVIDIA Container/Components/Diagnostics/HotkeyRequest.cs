using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

internal static class HotkeyRequest
{
    internal static (int Status, object Response) Dispatch(string name, string hotkeyId,
        IReadOnlyDictionary<string, string> headers, Func<JsonElement, bool> publish,
        Action<string> log, bool testMode, long? nowUnixMs = null)
    {
        var now = nowUnixMs ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var requestId = headers.TryGetValue("X-OSC-Request-Id", out var supplied) && IsSafeId(supplied)
            ? supplied : Guid.NewGuid().ToString("N");
        var createdAtUnixMs = now;
        if (headers.TryGetValue("X-OSC-Created-At", out var stamp) &&
            (!long.TryParse(stamp, NumberStyles.None, CultureInfo.InvariantCulture, out createdAtUnixMs) ||
             now - createdAtUnixMs > 2000 || createdAtUnixMs - now > 1000))
            return (409, new { handled = false, requestId, error = "stale hotkey request" });

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new[]
        {
            new { name = "/ShadowPlay/v.1.0/Hotkey", data = new
                { hotkeyId, hotKeyName = name, requestId, createdAtUnixMs } }
        }));
        bool delivered;
        try { delivered = publish?.Invoke(document.RootElement.Clone()) == true; }
        catch (Exception) { delivered = false; }
        log?.Invoke("[hotkey] id=" + requestId + " " + name + " -> " + (delivered ? "delivered" : "unavailable"));
        return delivered
            ? (200, new { handled = true, hotkeyId, requestId, testMode })
            : (503, new { handled = false, hotkeyId, requestId, error = "OSC delivery unavailable" });
    }

    private static bool IsSafeId(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 80) return false;
        foreach (var ch in value)
            if (!((ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z') ||
                  (ch >= '0' && ch <= '9') || ch is '-' or '_' or '.' or ':')) return false;
        return true;
    }
}
