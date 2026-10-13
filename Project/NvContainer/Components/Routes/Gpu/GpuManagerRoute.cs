using System.Diagnostics;
using System.Text.Json;
using Microsoft.Win32;
using NvGraphics.Abstractions;
using NvGraphics.Contracts;
using NvGraphics.Models;
using NvGraphics.Services;

internal static class GpuManagerRoute
{
    private static readonly object Gate = GpuProfilesRoute.StoreLock;
    // Adapter discovery may invoke DXGI/WMI and must not block profile-store requests.
    private static readonly object AdapterCacheGate = new();
    private static readonly GameProfileStore Store = new();
    private static readonly RegistryBackupService Backups = new();
    private static readonly Dictionary<string, string> LastRuntimeEvents =
        new(StringComparer.OrdinalIgnoreCase);
    private static List<GpuAdapterInfo> CachedAdapters;
    private static DxgiAdapterLuidResolver CachedResolver;
    private static string CachedMappingError = "";
    private static DateTimeOffset AdapterCacheUpdatedUtc;
    private static readonly WindowsGpuPreferenceService Preferences =
        new(new HkcuRegistryApi(), Backups);

    internal static (int Status, object Response) GetState()
    {
        try
        {
            GameProfile[] profiles;
            RegistryBackupRecord[] backups;
            lock (Gate)
            {
                profiles = Store.Load().Profiles.ToArray();
                backups = Backups.ListBackups().ToArray();
            }

            var adapterSnapshot = GetAdapterSnapshot();
            var adapters = adapterSnapshot.Adapters;
            var windowsApps = Preferences.ListPreferences()
                .Select(entry => new
                {
                    name = DisplayName(entry.Name),
                    exePath = entry.Name,
                    selectedAdapterId = !string.IsNullOrEmpty(
                        WindowsGpuPreferenceService.ParseSpecificAdapter(entry.Value as string))
                        ? adapters.FirstOrDefault(adapter => !string.IsNullOrEmpty(adapter.AdapterId) &&
                            string.Equals(
                            WindowsGpuPreferenceService.ToWindowsAdapterId(adapter.AdapterId),
                            WindowsGpuPreferenceService.ParseSpecificAdapter(entry.Value as string),
                            StringComparison.OrdinalIgnoreCase))?.AdapterId ?? ""
                        : "",
                    lastAppliedAdapterId =
                        WindowsGpuPreferenceService.ParseSpecificAdapter(entry.Value as string) ?? "",
                    windowsPreference = entry.Value as string ?? "",
                    registryValueKind = entry.Kind.ToString(),
                    iconDataUri = GpuProfilesRoute.GetExecutableIconDataUri(entry.Name),
                    source = "windows-graphics"
                })
                .ToArray();
            var telemetry = new GpuTelemetryService();
            var snapshot = telemetry.Sample();
            return (200, new
            {
                profiles = profiles.Select(profile =>
                    ProfileView(profile, IsExactPathProcessRunning(profile.ExePath),
                        GpuProfilesRoute.GetExecutableIconDataUri(profile.ExePath))).ToArray(),
                windowsApps,
                adapters = adapters.Select(adapter =>
                {
                    var sample = GpuTelemetryService.ForAdapter(snapshot, adapter);
                    return new
                    {
                        name = adapter.DisplayName ?? "",
                        adapterId = adapter.AdapterId ?? "",
                        pciLocation = adapter.PciLocation ?? adapter.PciBusId ?? "",
                        driverVersion = adapter.DriverVersion ?? "",
                        status = adapter.Status ?? "",
                        isSystemDefaultDisplayAdapter = adapter.IsSystemDefaultDisplayAdapter,
                        dxgiLuid = adapter.DxgiLuid ?? "",
                        utilizationPercent = sample?.UtilizationPercent,
                        memoryUsedMiB = sample?.MemoryUsedMiB,
                        memoryTotalMiB = sample?.MemoryTotalMiB
                    };
                }).ToArray(),
                telemetryAvailable = snapshot is not null,
                telemetryError = snapshot is null ? telemetry.LastError : "",
                backups = backups.Select(BackupView).ToArray()
            });
        }
        catch (Exception ex)
        {
            return (500, new { error = "Could not load GPU manager state: " + ex.Message });
        }
    }

    internal static (int Status, object Response) GetRunningProfilePaths()
    {
        try
        {
            string[] profilePaths;
            lock (Gate)
            {
                profilePaths = Store.Load().Profiles.Select(profile => profile.ExePath).ToArray();
            }
            var paths = profilePaths.Where(IsExactPathProcessRunning).ToArray();
            return (200, new { paths });
        }
        catch (Exception ex)
        {
            return (500, new { error = "Could not inspect running game profiles: " + ex.Message });
        }
    }

    internal static (int Status, object Response) SaveProfile(byte[] body)
    {
        if (!TryReadObject(body, out var root, out var error)) return (400, new { error });
        var exePath = ReadString(root, "exePath");
        var name = ReadString(root, "name");
        var adapterId = ReadString(root, "selectedAdapterId");
        if (!TryNormalizeExecutable(exePath, requireExisting: true, out exePath, out error))
            return (400, new { error });
        if (string.IsNullOrWhiteSpace(name) || name.Length > 128)
            return (400, new { error = "name must be 1-128 characters." });

        try
        {
            lock (Gate)
            {
                var data = Store.Load();
                var profile = data.Profiles.FirstOrDefault(item =>
                    string.Equals(Normalize(item.ExePath), Normalize(exePath), StringComparison.OrdinalIgnoreCase));
                if (profile is null)
                {
                    profile = new GameProfile { ExePath = exePath };
                    data.Profiles.Add(profile);
                }
                var normalizedAdapterId = string.IsNullOrWhiteSpace(adapterId) ? null : adapterId;
                var changedSelection = !string.Equals(profile.SelectedAdapterId, normalizedAdapterId,
                    StringComparison.OrdinalIgnoreCase);
                profile.Name = name.Trim();
                profile.ExePath = exePath;
                profile.SelectedAdapterId = normalizedAdapterId;
                if (profile.Status == PreferenceStatus.NotConfigured)
                {
                    profile.Status = PreferenceStatus.Configured;
                    profile.StatusMessage = "Profile saved. Windows GPU preference has not been changed.";
                }
                else if (changedSelection && profile.LastAppliedAdapterId is not null)
                {
                    profile.Status = PreferenceStatus.Configured;
                    profile.StatusMessage = "GPU selection saved. The Windows preference was not changed.";
                }
                Store.Save(data);
                return (200, new { saved = true, profile = ProfileView(profile) });
            }
        }
        catch (Exception ex)
        {
            return (500, new { error = "Could not save game profile: " + ex.Message });
        }
    }

    internal static (int Status, object Response) RemoveProfile(byte[] body,
        Action<JsonElement> publishEvents)
    {
        if (!TryReadObject(body, out var root, out var error)) return (400, new { error });
        var exePath = ReadString(root, "exePath");
        if (string.IsNullOrWhiteSpace(exePath)) return (400, new { error = "exePath is required." });
        try
        {
            GameProfile profile;
            bool hadWindowsPreference;
            lock (Gate)
            {
                var data = Store.Load();
                profile = data.Profiles.FirstOrDefault(item =>
                    string.Equals(Normalize(item.ExePath), Normalize(exePath), StringComparison.OrdinalIgnoreCase));
                if (profile is null) return (404, new { error = "Game profile was not found." });

                hadWindowsPreference = Preferences.FindValue(profile.ExePath) is not null;
                var preferenceResult = Preferences.RemoveEntry(profile.ExePath);
                if (!preferenceResult.Success)
                    return (409, new { error = preferenceResult.Message });

                int removed = data.Profiles.RemoveAll(item =>
                    string.Equals(Normalize(item.ExePath), Normalize(exePath), StringComparison.OrdinalIgnoreCase));
                if (removed == 0) return (404, new { error = "Game profile was not found." });
                Store.Save(data);
            }

            var publish = hadWindowsPreference
                ? PublishSystemDefault(profile, publishEvents)
                : (Status: 200, Response: (object)null);
            return (200, new
            {
                removed = true,
                windowsPreferenceRemoved = hadWindowsPreference,
                message = hadWindowsPreference
                    ? "Game profile and Windows GPU preference removed."
                    : "Game profile removed.",
                preferenceEventPublished = !hadWindowsPreference || publish.Status == 200
            });
        }
        catch (Exception ex)
        {
            return (500, new { error = "Could not remove game profile: " + ex.Message });
        }
    }

    internal static (int Status, object Response) ApplyPreference(byte[] body,
        Action<JsonElement> publishEvents)
    {
        if (!TryReadObject(body, out var root, out var error)) return (400, new { error });
        var exePath = ReadString(root, "exePath");
        var adapterId = ReadString(root, "selectedAdapterId");
        if (string.IsNullOrWhiteSpace(exePath) || string.IsNullOrWhiteSpace(adapterId))
            return (400, new { error = "exePath and selectedAdapterId are required." });

        try
        {
            // GPU discovery and event delivery are both potentially slow; neither belongs
            // inside the shared profile-store lock.
            var adapter = GetAdapterSnapshot().Adapters.FirstOrDefault(item =>
                string.Equals(item.AdapterId, adapterId, StringComparison.OrdinalIgnoreCase));
            if (adapter is null) return (400, new { error = "Selected GPU was not found." });

            GameProfile profile;
            string targetName;
            string statusMessage;
            lock (Gate)
            {
                var data = Store.Load();
                profile = data.Profiles.FirstOrDefault(item =>
                    string.Equals(Normalize(item.ExePath), Normalize(exePath), StringComparison.OrdinalIgnoreCase));
                var registryEntry = Preferences.ListPreferences().FirstOrDefault(item =>
                    string.Equals(item.Name, exePath, StringComparison.OrdinalIgnoreCase));
                if (profile is null && registryEntry is null)
                    return (404, new { error = "Game profile or Windows Graphics app was not found." });
                targetName = profile?.ExePath ?? registryEntry.Name;
                var result = Preferences.ApplyPreference(targetName, adapter.AdapterId);
                if (!result.Success) return (409, new { error = result.Message });

                statusMessage = result.Message + " Windows GPU preference verified.";
                if (profile is not null)
                {
                    profile.SelectedAdapterId = adapter.AdapterId;
                    profile.LastAppliedAdapterId = WindowsGpuPreferenceService.ToWindowsAdapterId(adapter.AdapterId);
                    profile.LastAppliedUtc = DateTime.UtcNow;
                    profile.Status = PreferenceStatus.Applied;
                    profile.StatusMessage = statusMessage;
                    Store.Save(data);
                }
            }

            var eventProfile = profile ?? new GameProfile
            {
                ExePath = targetName,
                Name = DisplayName(targetName)
            };
            var publish = PublishPreference(eventProfile, adapter, publishEvents);
            return (200, new
            {
                applied = true,
                message = statusMessage,
                preferenceEventPublished = publish.Status == 200,
                profile = profile is null ? null : ProfileView(profile)
            });
        }
        catch (Exception ex)
        {
            return (500, new { error = "Could not apply GPU preference: " + ex.Message });
        }
    }

    internal static (int Status, object Response) RemoveWindowsEntry(byte[] body,
        Action<JsonElement> publishEvents)
    {
        if (!TryReadObject(body, out var root, out var error)) return (400, new { error });
        var exePath = ReadString(root, "exePath");
        if (string.IsNullOrWhiteSpace(exePath)) return (400, new { error = "exePath is required." });
        GameProfile profile;
        lock (Gate)
        {
            var result = Preferences.RemoveEntry(exePath);
            if (!result.Success) return (409, new { error = result.Message });
            var data = Store.Load();
            profile = data.Profiles.FirstOrDefault(item =>
                string.Equals(Normalize(item.ExePath), Normalize(exePath), StringComparison.OrdinalIgnoreCase));
            if (profile is not null)
            {
                profile.Status = PreferenceStatus.Configured;
                profile.StatusMessage = result.Message;
                Store.Save(data);
            }
        }
        var targetProfile = profile ?? new GameProfile
        {
            ExePath = exePath,
            Name = DisplayName(exePath)
        };
        var publish = PublishSystemDefault(targetProfile, publishEvents);
        return (200, new
        {
            removed = true,
            message = "Windows GPU preference reset to system default.",
            preferenceEventPublished = publish.Status == 200
        });
    }

    internal static (int Status, object Response) RestoreBackup(byte[] body)
    {
        if (!TryReadObject(body, out var root, out var error)) return (400, new { error });
        var valueName = ReadString(root, "valueName");
        var timestampText = ReadString(root, "timestampUtc");
        if (!DateTime.TryParse(timestampText, null, System.Globalization.DateTimeStyles.RoundtripKind,
                out var timestamp))
            return (400, new { error = "timestampUtc is invalid." });
        var backup = Backups.ListBackups().FirstOrDefault(item =>
            item.TimestampUtc == timestamp.ToUniversalTime() &&
            string.Equals(item.ValueName, valueName, StringComparison.OrdinalIgnoreCase));
        if (backup is null) return (404, new { error = "Registry backup was not found." });
        if (!string.Equals(backup.SubKeyPath, WindowsGpuPreferenceService.KeyPath,
                StringComparison.OrdinalIgnoreCase))
            return (400, new { error = "Backup is not a GPU-preference registry backup." });
        var result = Preferences.RestoreBackup(backup);
        if (!result.Success) return (409, new { error = result.Message });

        string profileUpdateWarning = "";
        try
        {
            lock (Gate)
            {
                var data = Store.Load();
                var profile = data.Profiles.FirstOrDefault(item =>
                    string.Equals(Normalize(item.ExePath), Normalize(backup.ValueName),
                        StringComparison.OrdinalIgnoreCase));
                if (profile is not null)
                {
                    var currentValue = Preferences.FindValue(profile.ExePath);
                    profile.Status = currentValue is null
                        ? PreferenceStatus.Configured : PreferenceStatus.Applied;
                    profile.LastAppliedAdapterId = WindowsGpuPreferenceService.ParseSpecificAdapter(currentValue);
                    profile.LastAppliedUtc = currentValue is null ? null : profile.LastAppliedUtc ?? DateTime.UtcNow;
                    profile.StatusMessage = "Registry backup restored. Windows GPU preference refreshed.";
                    Store.Save(data);
                }
            }
        }
        catch (Exception ex)
        {
            profileUpdateWarning = " Profile status could not be refreshed: " + ex.Message;
        }
        return (200, new { restored = true, message = result.Message + profileUpdateWarning });
    }

    internal static (int Status, object Response) CheckRuntime(byte[] body,
        Action<JsonElement> publishEvents)
    {
        if (!TryReadObject(body, out var root, out var error)) return (400, new { error });
        var exePath = ReadString(root, "exePath");
        if (!TryNormalizeExecutable(exePath, requireExisting: true, out exePath, out error))
            return (400, new { error });
        if (!IsExactPathProcessRunning(exePath))
            return (409, new { error = "The game is not currently running at the saved executable path." });

        try
        {
            var adapterSnapshot = GetAdapterSnapshot();
            var resolver = adapterSnapshot.Resolver;
            if (resolver.ResolvedCount == 0)
                return (503, new { error = "Runtime GPU check unavailable: " + adapterSnapshot.MappingError });
            var result = new RuntimeGpuMonitorService(new GpuEngineCounterProvider(), resolver,
                new ProcessPathResolver(), new RuntimeGpuMonitorOptions
                {
                    WindowSamples = 2,
                    PollInterval = TimeSpan.FromMilliseconds(250)
                }).Check(exePath);
            GameProfile profile;
            lock (Gate)
                profile = Store.Load().Profiles.FirstOrDefault(item =>
                    string.Equals(Normalize(item.ExePath), Normalize(exePath), StringComparison.OrdinalIgnoreCase));
            var name = profile?.Name ?? Path.GetFileNameWithoutExtension(exePath);
            var runtimeTuple = result.State + "|" +
                string.Join("|", result.Adapters.Select(adapter => adapter.AdapterId));
            var shouldPublish = false;
            lock (Gate)
            {
                if (!LastRuntimeEvents.TryGetValue(exePath, out var last) || last != runtimeTuple)
                {
                    LastRuntimeEvents[exePath] = runtimeTuple;
                    shouldPublish = true;
                }
            }
            if (shouldPublish)
            {
                var payload = GpuStatePublisher.FromResult(result, exePath, name);
                using var document = JsonDocument.Parse(JsonSerializer.Serialize(new[]
                {
                    new { name = GpuRuntimeContract.EventName, data = payload }
                }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
                publishEvents?.Invoke(document.RootElement.Clone());
            }
            lock (Gate)
            {
                var data = Store.Load();
                var storedProfile = data.Profiles.FirstOrDefault(item =>
                    string.Equals(Normalize(item.ExePath), Normalize(exePath), StringComparison.OrdinalIgnoreCase));
                if (storedProfile is not null)
                {
                    storedProfile.Status = result.State == RuntimeGpuState.Verified
                        ? PreferenceStatus.RuntimeVerified : PreferenceStatus.RuntimeNotVerified;
                    storedProfile.StatusMessage = $"Runtime {result.State}: {result.Reason}";
                    Store.Save(data);
                }
            }
            return (200, new
            {
                state = result.State.ToString(),
                reason = result.Reason,
                adapters = result.Adapters.Select(adapter => new
                {
                    name = adapter.DisplayName ?? "",
                    adapterId = adapter.AdapterId ?? ""
                }).ToArray(),
                windowDurationMs = (int)result.WindowDuration.TotalMilliseconds
            });
        }
        catch (Exception ex)
        {
            return (500, new { error = "Runtime GPU check failed: " + ex.Message });
        }
    }

    private static object ProfileView(GameProfile profile, bool isRunning = false, string iconDataUri = null) => new
    {
        name = profile.Name,
        exePath = profile.ExePath,
        iconDataUri,
        selectedAdapterId = profile.SelectedAdapterId ?? "",
        lastAppliedAdapterId = profile.LastAppliedAdapterId ?? "",
        lastAppliedUtc = profile.LastAppliedUtc,
        status = profile.Status.ToString(),
        statusMessage = profile.StatusMessage ?? "",
        isRunning
    };

    private static object BackupView(RegistryBackupRecord backup) => new
    {
        timestampUtc = backup.TimestampUtc,
        valueName = backup.ValueName,
        reason = backup.Reason,
        valueExisted = backup.ValueExisted
    };

    private static (List<GpuAdapterInfo> Adapters, DxgiAdapterLuidResolver Resolver, string MappingError)
        GetAdapterSnapshot()
    {
        lock (AdapterCacheGate)
        {
            if (CachedAdapters is not null &&
                DateTimeOffset.UtcNow - AdapterCacheUpdatedUtc < TimeSpan.FromSeconds(30))
                return (CachedAdapters, CachedResolver, CachedMappingError);

            CachedAdapters = new GpuDiscoveryService().DiscoverAdapters();
            CachedResolver = DxgiAdapterLuidResolver.Create(CachedAdapters, out CachedMappingError);
            foreach (var pair in CachedResolver.ResolvedByLuid) pair.Value.DxgiLuid = pair.Key;
            AdapterCacheUpdatedUtc = DateTimeOffset.UtcNow;
            return (CachedAdapters, CachedResolver, CachedMappingError);
        }
    }

    private static (int Status, object Response) PublishPreference(GameProfile profile,
        GpuAdapterInfo adapter, Action<JsonElement> publishEvents)
    {
        var payload = new GpuPreferencePayload
        {
            SchemaVersion = GpuPreferenceContract.CurrentSchemaVersion,
            EventId = Guid.NewGuid().ToString("N"),
            ExePath = profile.ExePath,
            DisplayName = profile.Name,
            AdapterId = WindowsGpuPreferenceService.ToWindowsAdapterId(adapter.AdapterId),
            AdapterName = adapter.DisplayName ?? adapter.AdapterId,
            AppliedUtc = DateTimeOffset.UtcNow
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        return GpuRuntimeRoute.HandlePreference(bytes, publishEvents, _ => { });
    }

    private static (int Status, object Response) PublishSystemDefault(GameProfile profile,
        Action<JsonElement> publishEvents)
    {
        var payload = new GpuPreferencePayload
        {
            SchemaVersion = GpuPreferenceContract.CurrentSchemaVersion,
            EventId = Guid.NewGuid().ToString("N"),
            ExePath = profile.ExePath,
            DisplayName = profile.Name,
            IsSystemDefault = true,
            AppliedUtc = DateTimeOffset.UtcNow
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        return GpuRuntimeRoute.HandlePreference(bytes, publishEvents, _ => { });
    }

    private static bool IsExactPathProcessRunning(string exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath)) return false;
        string expected;
        try { expected = Path.GetFullPath(exePath); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        { return false; }
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(expected)))
        {
            using (process)
            {
                try
                {
                    if (string.Equals(Path.GetFullPath(
                                GpuProfilesRoute.GetExecutablePath(process) ?? ""), expected,
                            StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                catch { }
            }
        }
        return false;
    }

    private static bool TryNormalizeExecutable(string input, bool requireExisting,
        out string fullPath, out string error)
    {
        fullPath = null;
        error = null;
        try
        {
            if (string.IsNullOrWhiteSpace(input) || !Path.IsPathRooted(input) ||
                !string.Equals(Path.GetExtension(input), ".exe", StringComparison.OrdinalIgnoreCase))
            {
                error = "exePath must be an absolute .exe path.";
                return false;
            }
            fullPath = Path.GetFullPath(input);
            if (requireExisting && !File.Exists(fullPath))
            {
                error = "Executable does not exist.";
                return false;
            }
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = "exePath is invalid.";
            return false;
        }
    }

    private static bool TryReadObject(byte[] body, out JsonElement root, out string error)
    {
        root = default;
        error = "Invalid JSON object.";
        try
        {
            using var document = JsonDocument.Parse(body ?? Array.Empty<byte>());
            if (document.RootElement.ValueKind != JsonValueKind.Object) return false;
            root = document.RootElement.Clone();
            return true;
        }
        catch (JsonException) { return false; }
    }

    private static string ReadString(JsonElement root, string property) =>
        root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? "" : "";

    private static string Normalize(string path)
    {
        try { return Path.GetFullPath(path ?? "").TrimEnd(Path.DirectorySeparatorChar); }
        catch { return path ?? ""; }
    }

    private static string DisplayName(string appId)
    {
        if (string.IsNullOrWhiteSpace(appId)) return "";
        var name = Path.GetFileNameWithoutExtension(appId);
        if (string.IsNullOrWhiteSpace(name) || string.Equals(name, appId, StringComparison.Ordinal))
        {
            var separator = appId.LastIndexOf('!');
            if (separator >= 0 && separator + 1 < appId.Length) name = appId[(separator + 1)..];
        }
        return string.IsNullOrWhiteSpace(name) ? appId : name;
    }
}
