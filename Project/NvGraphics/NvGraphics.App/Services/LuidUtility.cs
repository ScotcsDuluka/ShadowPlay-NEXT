namespace NvGraphics.Services;

/// <summary>LUID parsing/normalization shared by DXGI enumeration and counter parsing.</summary>
public static class LuidUtility
{
    /// <summary>
    /// Parses a LUID from a GPU Engine counter instance segment, e.g. "0x00000000_0x00013667"
    /// (high part first). Returns the canonical lowercase form, or null when malformed.
    /// Accepts uppercase hex; requires both parts and the 0x prefixes.
    /// </summary>
    public static string NormalizeCounterLuid(string luid)
    {
        if (string.IsNullOrWhiteSpace(luid)) return null;
        var parts = luid.Split('_');
        if (parts.Length != 2) return null;
        if (!TryParsePart(parts[0], out uint high)) return null;
        if (!TryParsePart(parts[1], out uint low)) return null;
        return $"0x{high:x8}_0x{low:x8}";
    }

    /// <summary>Canonical form from raw DXGI LUID parts (LowPart/HighPart).</summary>
    public static string FromParts(uint lowPart, uint highPart) => $"0x{highPart:x8}_0x{lowPart:x8}";

    private static bool TryParsePart(string s, out uint value)
    {
        value = 0;
        if (!s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) return false;
        return uint.TryParse(s.Substring(2), System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture, out value);
    }

    /// <summary>
    /// Extracts the hardware identity tokens from a PnP adapter id
    /// "VEN_10DE&amp;DEV_1B06&amp;SUBSYS_85F11043" → ["10DE","1B06","85F11043"] (lowercase),
    /// or from an already-Windows-form id "10DE&amp;1B06&amp;85F11043".
    /// </summary>
    public static IReadOnlyList<string> HardwareTokens(string adapterId)
    {
        if (string.IsNullOrEmpty(adapterId)) return Array.Empty<string>();
        return adapterId
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim())
            .Where(t => t.Length > 0)
            .Select(t =>
            {
                int us = t.IndexOf('_');
                return (us >= 0 && us + 1 < t.Length) ? t.Substring(us + 1) : t;
            })
            .Select(t => t.ToLowerInvariant())
            .ToList();
    }
}
