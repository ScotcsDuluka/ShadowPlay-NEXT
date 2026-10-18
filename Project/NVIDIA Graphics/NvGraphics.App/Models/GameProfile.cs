namespace NvGraphics.Models;

/// <summary>A per-game GPU preference profile (versioned JSON on disk).</summary>
public sealed class GameProfile
{
    public string Name { get; set; } = "";
    /// <summary>Absolute path of the game executable — the registry key is keyed by this.</summary>
    public string ExePath { get; set; } = "";

    /// <summary>AdapterId (VEN&amp;DEV&amp;SUBSYS) of the chosen GPU, or null for system default.</summary>
    public string SelectedAdapterId { get; set; }

    /// <summary>Last SpecificAdapter value actually written to Windows and verified, if any.</summary>
    public string LastAppliedAdapterId { get; set; }

    public DateTime? LastAppliedUtc { get; set; }
    public PreferenceStatus Status { get; set; } = PreferenceStatus.NotConfigured;
    public string StatusMessage { get; set; } = "";
}

/// <summary>On-disk container for GameProfileStore.</summary>
public sealed class GameProfileStoreData
{
    public int SchemaVersion { get; set; } = 1;
    public List<GameProfile> Profiles { get; set; } = new();
}
