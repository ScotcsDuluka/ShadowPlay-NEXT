using System.Text.Json;
using Microsoft.Win32;

namespace NvGraphics.Services;

public sealed class RegistryBackupRecord
{
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
    public string SubKeyPath { get; set; } = "";
    public string ValueName { get; set; } = "";
    public RegistryValueKind Kind { get; set; }
    /// <summary>Previous raw value: string, string[] (for multi-string) or base64 (binary).
    /// Meaningful only when ValueExisted is true.</summary>
    public string PreviousValue { get; set; }
    /// <summary>False when the value did not exist before the operation — restore must
    /// then delete the value instead of writing one back.</summary>
    public bool ValueExisted { get; set; } = true;
    public string Reason { get; set; } = "";
}

/// <summary>
/// Captures previous UserGpuPreferences values into JSON files under the NvGraphics
/// app-data directory before any change, and restores them on demand.
/// </summary>
public sealed class RegistryBackupService
{
    public string BackupDirectory { get; }

    public RegistryBackupService(string backupDirectory = null)
    {
        BackupDirectory = backupDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NVIDIA ShadowPlay", "NvGraphics", "registry-backups");
    }

    public RegistryBackupRecord SaveBackup(string subKeyPath, string valueName,
        object previousValue, RegistryValueKind? kind, string reason)
    {
        var record = new RegistryBackupRecord
        {
            SubKeyPath = subKeyPath,
            ValueName = valueName,
            Kind = kind ?? RegistryValueKind.String,
            PreviousValue = Encode(previousValue),
            ValueExisted = previousValue != null || kind != null,
            Reason = reason
        };
        Directory.CreateDirectory(BackupDirectory);
        string file = Path.Combine(BackupDirectory,
            $"{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}-{Sanitize(valueName)}.json");
        File.WriteAllText(file, JsonSerializer.Serialize(record, new JsonSerializerOptions { WriteIndented = true }));
        return record;
    }

    public List<RegistryBackupRecord> ListBackups()
    {
        if (!Directory.Exists(BackupDirectory)) return new();
        var list = new List<RegistryBackupRecord>();
        foreach (var f in Directory.GetFiles(BackupDirectory, "*.json"))
        {
            try
            {
                var r = JsonSerializer.Deserialize<RegistryBackupRecord>(File.ReadAllText(f));
                if (r != null) list.Add(r);
            }
            catch (JsonException)
            {
                // Corrupt or unrelated JSON file in the backup directory — skip it.
            }
        }
        return list.OrderByDescending(r => r.TimestampUtc).ToList();
    }

    /// <summary>Decodes a backup record's stored value back into a registry value object.</summary>
    public static object DecodeValue(RegistryBackupRecord record) => record.Kind switch
    {
        RegistryValueKind.MultiString => JsonSerializer.Deserialize<string[]>(record.PreviousValue),
        RegistryValueKind.Binary => Convert.FromBase64String(record.PreviousValue),
        RegistryValueKind.DWord => int.Parse(record.PreviousValue),
        RegistryValueKind.QWord => long.Parse(record.PreviousValue),
        _ => record.PreviousValue
    };

    private static string Encode(object value) => value switch
    {
        null => "",
        string[] multi => JsonSerializer.Serialize(multi),
        byte[] bin => Convert.ToBase64String(bin),
        _ => value.ToString()
    };

    private static string Sanitize(string name) =>
        string.Concat(name.Select(c => char.IsLetterOrDigit(c) ? c : '_'));
}
