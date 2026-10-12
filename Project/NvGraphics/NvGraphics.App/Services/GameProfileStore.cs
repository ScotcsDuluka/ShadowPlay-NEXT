using System.Text.Json;
using NvGraphics.Models;

namespace NvGraphics.Services;

/// <summary>
/// Versioned JSON persistence for game profiles in a dedicated NvGraphics app-data directory:
/// %LOCALAPPDATA%\NVIDIA ShadowPlay\GPU Manager\profiles.json
/// </summary>
public sealed class GameProfileStore
{
    public const int CurrentSchemaVersion = 1;
    public string StorePath { get; }
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public GameProfileStore(string storePath = null)
    {
        StorePath = storePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NVIDIA ShadowPlay", "NvGraphics", "profiles.json");
    }

    public GameProfileStoreData Load()
    {
        if (!File.Exists(StorePath)) return new GameProfileStoreData();
        var data = JsonSerializer.Deserialize<GameProfileStoreData>(File.ReadAllText(StorePath));
        if (data is null) throw new InvalidOperationException("Profile store deserialized to null.");
        if (data.SchemaVersion > CurrentSchemaVersion)
            throw new NotSupportedException(
                $"Profile store schema v{data.SchemaVersion} is newer than supported v{CurrentSchemaVersion}.");
        if (data.SchemaVersion < CurrentSchemaVersion) data.SchemaVersion = CurrentSchemaVersion;
        return data;
    }

    public void Save(GameProfileStoreData data)
    {
        data.SchemaVersion = CurrentSchemaVersion;
        Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
        File.WriteAllText(StorePath, JsonSerializer.Serialize(data, JsonOpts));
    }
}
