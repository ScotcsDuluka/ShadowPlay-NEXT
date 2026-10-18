using NvGraphics.Abstractions;
using NvGraphics.Models;
using Microsoft.Win32;

namespace NvGraphics.Services;

public sealed class PreferenceResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = "";
    /// <summary>Raw value data for the executable as read back after a write, when verification ran.</summary>
    public string VerifiedValue { get; init; }
}

/// <summary>
/// Manages HKCU\Software\Microsoft\DirectX\UserGpuPreferences per-application GPU preferences.
///
/// Windows schema (verified against the live key): UserGpuPreferences is a KEY; each
/// application is an individual value NAME (full executable path, or a UWP AUMID) whose
/// REG_SZ data holds that app's tokens, e.g.
///   "C:\...\game.exe" = "SpecificAdapter=10DE&amp;1B06&amp;85F11043;GpuPreference=1073741824;"
/// Only the target executable's value is ever read or written; unrelated values and
/// non-REG_SZ values (e.g. GraphicsFeaturesNotificationConfig DWORD) are never touched.
/// A write only counts as applied after a read-back verification.
/// </summary>
public sealed class WindowsGpuPreferenceService
{
    public const string KeyPath = @"Software\Microsoft\DirectX\UserGpuPreferences";
    public const string GpuPerformanceValue = "1073741824";

    private readonly IRegistryApi _registry;
    private readonly RegistryBackupService _backups;

    public WindowsGpuPreferenceService(IRegistryApi registry, RegistryBackupService backups)
    {
        _registry = registry;
        _backups = backups;
    }

    // ---------- adapter id forms ----------

    /// <summary>
    /// Converts the PnP adapter id "VEN_10DE&amp;DEV_1B06&amp;SUBSYS_85F11043" (discovery identity)
    /// to the Windows SpecificAdapter form "10DE&amp;1B06&amp;85F11043". Returns the input
    /// unchanged when it is already in Windows form or cannot be converted.
    /// </summary>
    public static string ToWindowsAdapterId(string pnpAdapterId)
    {
        if (string.IsNullOrEmpty(pnpAdapterId)) return pnpAdapterId;
        var tokens = pnpAdapterId.Split('&')
            .Select(t => t.StartsWith("VEN_") || t.StartsWith("DEV_") || t.StartsWith("SUBSYS_")
                ? t.Substring(t.IndexOf('_') + 1)
                : t)
            .Where(t => t.Length > 0);
        return string.Join("&", tokens);
    }

    // ---------- value data parsing / building ----------

    /// <summary>Reads the SpecificAdapter token (Windows id form) from one value's data, or null.</summary>
    public static string ParseSpecificAdapter(string valueData)
    {
        if (string.IsNullOrWhiteSpace(valueData)) return null;
        foreach (var part in valueData.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split('=', 2);
            if (kv.Length == 2 && kv[0].Trim().Equals("SpecificAdapter", StringComparison.OrdinalIgnoreCase))
                return kv[1].Trim();
        }
        return null;
    }

    /// <summary>
    /// Rebuilds value data with the given SpecificAdapter (Windows id form), preserving
    /// unrelated tokens while replacing stale SpecificAdapter/GpuPreference tokens.
    /// </summary>
    public static string BuildValueData(string windowsAdapterId, string existingData = null)
    {
        var kept = new List<string>();
        bool hadGpuPreference = false;
        if (existingData != null)
        {
            foreach (var part in existingData.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = part.Split('=', 2);
                if (kv.Length == 2)
                {
                    if (kv[0].Trim().Equals("SpecificAdapter", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (kv[0].Trim().Equals("GpuPreference", StringComparison.OrdinalIgnoreCase))
                    {
                        hadGpuPreference = true;
                        continue;
                    }
                }
                kept.Add(part.Trim());
            }
        }
        var tail = kept.Count > 0 ? string.Join(";", kept) + ";" : "";
        var gpuPreference = windowsAdapterId is not null || hadGpuPreference
            ? $"GpuPreference={GpuPerformanceValue};"
            : "";
        return (windowsAdapterId is null ? "" : $"SpecificAdapter={windowsAdapterId};") +
            gpuPreference + tail;
    }

    // ---------- read / apply / remove / restore ----------

    /// <summary>Returns the value data for an executable, or null when not configured.
    /// Non-REG_SZ values are reported as unsupported rather than returned as data.</summary>
    public string FindValue(string exePath)
    {
        var (raw, kind) = _registry.GetValue(KeyPath, exePath);
        if (raw is null) return null;
        if (kind != RegistryValueKind.String)
            throw new FormatException(
                $"'{exePath}' exists as {kind} in UserGpuPreferences; only REG_SZ values are supported. " +
                "The value was left untouched.");
        return (string)raw;
    }

    /// <summary>Returns all Windows Graphics app entries without modifying the registry.</summary>
    public IReadOnlyList<RegistryValueEntry> ListPreferences() =>
        _registry.GetValues(KeyPath)
            .Where(entry => entry.Kind == RegistryValueKind.String &&
                !string.IsNullOrWhiteSpace(entry.Name))
            .ToArray();

    /// <summary>
    /// Applies a SpecificAdapter preference for the executable. Only the target value name is
    /// written; the previous value is backed up first; success requires a read-back match.
    /// </summary>
    public PreferenceResult ApplyPreference(string exePath, string pnpAdapterId)
    {
        try
        {
            exePath = exePath?.Trim();
            if (string.IsNullOrEmpty(exePath))
                return new PreferenceResult { Success = false, Message = "Executable path is empty." };

            var (raw, kind) = _registry.GetValue(KeyPath, exePath);
            if (raw is not null && kind != RegistryValueKind.String)
                return new PreferenceResult
                {
                    Success = false,
                    Message = $"'{exePath}' is stored as {kind}; only REG_SZ is supported. Nothing was changed."
                };

            string windowsId = ToWindowsAdapterId(pnpAdapterId);
            _backups.SaveBackup(KeyPath, exePath, raw, kind,
                $"apply SpecificAdapter={windowsId ?? "(default)"} for {exePath}");

            string newData = BuildValueData(windowsId, raw as string);
            _registry.SetValue(KeyPath, exePath, newData, RegistryValueKind.String);

            var (readBack, readKind) = _registry.GetValue(KeyPath, exePath);
            bool verified = readKind == RegistryValueKind.String
                && string.Equals((string)readBack, newData, StringComparison.Ordinal)
                && string.Equals(ParseSpecificAdapter((string)readBack), windowsId, StringComparison.OrdinalIgnoreCase);
            return new PreferenceResult
            {
                Success = verified,
                Message = verified
                    ? $"Preference applied to '{exePath}' and verified by reading the value back."
                    : "Registry write did not verify: read-back does not match the written data.",
                VerifiedValue = readBack as string
            };
        }
        catch (Exception ex)
        {
            return new PreferenceResult { Success = false, Message = $"Could not apply preference: {ex.Message}" };
        }
    }

    /// <summary>Deletes only the executable's value (backing up first). Missing entry = no-op.</summary>
    public PreferenceResult RemoveEntry(string exePath)
    {
        try
        {
            exePath = exePath?.Trim();
            var (raw, kind) = _registry.GetValue(KeyPath, exePath);
            if (raw is null)
                return new PreferenceResult { Success = true, Message = "No registry value existed for this executable." };

            if (kind != RegistryValueKind.String)
                return new PreferenceResult
                {
                    Success = false,
                    Message = $"'{exePath}' is stored as {kind}; only REG_SZ is supported. Nothing was deleted."
                };

            _backups.SaveBackup(KeyPath, exePath, raw, kind, $"remove entry for {exePath}");
            _registry.DeleteValue(KeyPath, exePath);

            bool verified = _registry.GetValue(KeyPath, exePath).Value is null;
            return new PreferenceResult
            {
                Success = verified,
                Message = verified ? "Entry removed and verified." : "Entry still present after deletion."
            };
        }
        catch (Exception ex)
        {
            return new PreferenceResult { Success = false, Message = $"Could not remove entry: {ex.Message}" };
        }
    }

    /// <summary>
    /// Restores a recorded backup exactly: rewrites the prior value, or — when the value did
    /// not exist before the backed-up operation — deletes only that value name.
    /// </summary>
    public PreferenceResult RestoreBackup(RegistryBackupRecord record)
    {
        try
        {
            if (record.ValueExisted)
            {
                var current = _registry.GetValue(record.SubKeyPath, record.ValueName);
                _backups.SaveBackup(record.SubKeyPath, record.ValueName, current.Value, current.Kind,
                    $"auto-backup before restore of {record.TimestampUtc:u}");
                var previous = RegistryBackupService.DecodeValue(record);
                _registry.SetValue(record.SubKeyPath, record.ValueName, previous, record.Kind);
                var (readBack, readKind) = _registry.GetValue(record.SubKeyPath, record.ValueName);
                bool verified = readKind == record.Kind
                    && string.Equals(ReadString(readBack), ReadString(previous), StringComparison.Ordinal);
                return new PreferenceResult
                {
                    Success = verified,
                    Message = verified ? "Previous value restored." : "Restore did not verify after write."
                };
            }
            else
            {
                _registry.DeleteValue(record.SubKeyPath, record.ValueName);
                bool verified = _registry.GetValue(record.SubKeyPath, record.ValueName).Value is null;
                return new PreferenceResult
                {
                    Success = verified,
                    Message = verified
                        ? "Value removed (it did not exist before the backed-up operation)."
                        : "Value still present after removal."
                };
            }
        }
        catch (Exception ex)
        {
            return new PreferenceResult { Success = false, Message = $"Could not restore backup: {ex.Message}" };
        }
    }

    private static string ReadString(object v) => v as string ?? v?.ToString() ?? "";
}
