using System.Security.Cryptography;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using NvGraphics.Models;

namespace NvGraphics.Services;

/// <summary>
/// Shared per-user local profile store. Atomic replace protects file integrity;
/// a cross-process lock + optimistic fingerprint additionally protects from lost updates.
/// Callers must reload when another process has saved the file in the meantime.
/// </summary>
public sealed class GameProfileStore
{
    public const int CurrentSchemaVersion = 1;
    public string StorePath { get; }
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
    private readonly IProfileStoreFileOperations _fileOperations;
    private static readonly ConditionalWeakTable<GameProfileStoreData, SnapshotVersion> Versions = new();

    private sealed class SnapshotVersion
    {
        internal string Fingerprint = "";
    }

    public GameProfileStore(string storePath = null)
        : this(storePath, new ProfileStoreFileOperations()) { }

    internal GameProfileStore(string storePath, IProfileStoreFileOperations fileOperations)
    {
        StorePath = Path.GetFullPath(storePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NVIDIA ShadowPlay", "NvGraphics", "profiles.json"));
        _fileOperations = fileOperations ?? throw new ArgumentNullException(nameof(fileOperations));
    }

    public GameProfileStoreData Load()
    {
        using var lockHandle = AcquireStoreLock();
        return LoadCore();
    }

    private GameProfileStoreData LoadCore()
    {
        var bytes = File.Exists(StorePath) ? File.ReadAllBytes(StorePath) : null;
        var data = bytes is null ? new GameProfileStoreData()
            : JsonSerializer.Deserialize<GameProfileStoreData>(bytes);
        if (data is null)
            throw new InvalidOperationException("Profile store deserialized to null.");
        if (data.SchemaVersion > CurrentSchemaVersion)
            throw new NotSupportedException(
                $"Profile store schema v{data.SchemaVersion} is newer than supported v{CurrentSchemaVersion}.");
        if (data.SchemaVersion < CurrentSchemaVersion) data.SchemaVersion = CurrentSchemaVersion;
        data.Profiles ??= new List<GameProfile>();
        Versions.Add(data, new SnapshotVersion { Fingerprint = Hash(bytes) });
        return data;
    }

    /// <summary>Apply one logical edit to the latest on-disk profiles while
    /// holding the cross-process lock. Preserves unrelated concurrent edits.</summary>
    public GameProfileStoreData Update(Func<GameProfileStoreData, bool> edit)
    {
        ArgumentNullException.ThrowIfNull(edit);
        using var lockHandle = AcquireStoreLock();
        var fresh = LoadCore();
        if (edit(fresh)) SaveCore(fresh);
        return fresh;
    }

    public void Save(GameProfileStoreData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        using var lockHandle = AcquireStoreLock();
        SaveCore(data);
    }

    private void SaveCore(GameProfileStoreData data)
    {
        var currentBytes = File.Exists(StorePath) ? File.ReadAllBytes(StorePath) : null;
        var currentFingerprint = Hash(currentBytes);
        // A new, unversioned snapshot may create a new store but cannot overwrite
        // one another process already owns. Loaded snapshots carry a baseline hash.
        if (!Versions.TryGetValue(data, out var version))
        {
            if (currentBytes is not null)
                throw new IOException("Game Profiles changed in another process. Reload before saving.");
            version = new SnapshotVersion { Fingerprint = currentFingerprint };
            Versions.Add(data, version);
        }
        if (!string.Equals(version.Fingerprint, currentFingerprint, StringComparison.Ordinal))
            throw new IOException("Game Profiles changed in another process. Reload before saving.");

        data.SchemaVersion = CurrentSchemaVersion;
        var serialized = JsonSerializer.SerializeToUtf8Bytes(data, JsonOpts);
        var destination = StorePath;
        var directory = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory,
            "." + Path.GetFileName(destination) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = _fileOperations.CreateTemporaryFile(temporary))
            {
                stream.Write(serialized, 0, serialized.Length);
                _fileOperations.FlushToDisk(stream);
            }
            if (_fileOperations.FileExists(destination))
                _fileOperations.Replace(temporary, destination);
            else
                _fileOperations.Move(temporary, destination);
            version.Fingerprint = Hash(serialized);
        }
        catch (Exception operationException)
        {
            try { _fileOperations.DeleteIfExists(temporary); }
            catch (Exception cleanupException)
            {
                throw new IOException("Could not save profile store or clean up temporary file.",
                    new AggregateException(operationException, cleanupException));
            }
            ExceptionDispatchInfo.Capture(operationException).Throw();
            throw;
        }
    }

    private static string Hash(byte[] data)
        => data is null ? "(missing)" : Convert.ToHexString(SHA256.HashData(data));

    private FileStream AcquireStoreLock()
    {
        var directory = Path.GetDirectoryName(StorePath)!;
        Directory.CreateDirectory(directory);
        var lockName = StorePath + ".lock";
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            try
            {
                return new FileStream(lockName, FileMode.OpenOrCreate,
                    FileAccess.ReadWrite, FileShare.None, bufferSize: 1);
            }
            catch (IOException) when (stopwatch.Elapsed < TimeSpan.FromSeconds(5))
            {
                Thread.Sleep(40);
            }
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
        new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            4096, FileOptions.WriteThrough);
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
