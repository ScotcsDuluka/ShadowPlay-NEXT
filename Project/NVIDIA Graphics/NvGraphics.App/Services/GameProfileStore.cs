using System.Text.Json;
using System.Runtime.ExceptionServices;
using NvGraphics.Models;

namespace NvGraphics.Services;

/// <summary>
/// Versioned JSON persistence for game profiles in a dedicated NvGraphics app-data directory:
/// %LOCALAPPDATA%\NVIDIA ShadowPlay\NvGraphics\profiles.json
/// </summary>
public sealed class GameProfileStore
{
    public const int CurrentSchemaVersion = 1;
    public string StorePath { get; }
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
    private readonly IProfileStoreFileOperations _fileOperations;

    public GameProfileStore(string storePath = null)
        : this(storePath, new ProfileStoreFileOperations())
    {
    }

    internal GameProfileStore(string storePath, IProfileStoreFileOperations fileOperations)
    {
        StorePath = storePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NVIDIA ShadowPlay", "NvGraphics", "profiles.json");
        _fileOperations = fileOperations ?? throw new ArgumentNullException(nameof(fileOperations));
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
        if (data.Profiles is null) data.Profiles = new List<GameProfile>();
        return data;
    }

    public void Save(GameProfileStoreData data)
    {
        data.SchemaVersion = CurrentSchemaVersion;
        var serialized = JsonSerializer.SerializeToUtf8Bytes(data, JsonOpts);
        var destinationPath = Path.GetFullPath(StorePath);

        if (_fileOperations.FileExists(destinationPath))
            Load();

        var directory = Path.GetDirectoryName(destinationPath)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(
            directory, "." + Path.GetFileName(destinationPath) + "." + Guid.NewGuid().ToString("N") + ".tmp");

        try
        {
            using (var stream = _fileOperations.CreateTemporaryFile(temporaryPath))
            {
                stream.Write(serialized, 0, serialized.Length);
                _fileOperations.FlushToDisk(stream);
            }

            if (_fileOperations.FileExists(destinationPath))
                _fileOperations.Replace(temporaryPath, destinationPath);
            else
                _fileOperations.Move(temporaryPath, destinationPath);
        }
        catch (Exception operationException)
        {
            try
            {
                _fileOperations.DeleteIfExists(temporaryPath);
            }
            catch (Exception cleanupException)
            {
                throw new IOException(
                    $"Could not save profile store, and temporary file cleanup also failed: {temporaryPath}",
                    new AggregateException(operationException, cleanupException));
            }

            ExceptionDispatchInfo.Capture(operationException).Throw();
            throw;
        }
    }
}

internal interface IProfileStoreFileOperations
{
    bool FileExists(string path);
    Stream CreateTemporaryFile(string path);
    void FlushToDisk(Stream stream);
    void Replace(string temporaryPath, string destinationPath);
    void Move(string temporaryPath, string destinationPath);
    void DeleteIfExists(string path);
}

internal sealed class ProfileStoreFileOperations : IProfileStoreFileOperations
{
    public bool FileExists(string path) => File.Exists(path);

    public Stream CreateTemporaryFile(string path) =>
        new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);

    public void FlushToDisk(Stream stream) => ((FileStream)stream).Flush(flushToDisk: true);

    public void Replace(string temporaryPath, string destinationPath) =>
        File.Replace(temporaryPath, destinationPath, destinationBackupFileName: null);

    public void Move(string temporaryPath, string destinationPath) =>
        File.Move(temporaryPath, destinationPath);

    public void DeleteIfExists(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }
}
