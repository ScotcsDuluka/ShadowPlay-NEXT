using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Management;

internal static class NvContainerGpuProfilesRoute
{
    internal static readonly object StoreLock = new object();
    private static readonly object IconCacheLock = new object();
    private static readonly Dictionary<string, string> ExecutableIconCache =
        new(StringComparer.OrdinalIgnoreCase);
    private const int PreferredExecutableIconSize = 256;
    private const int CurrentSchemaVersion = 1;
    private static readonly string DefaultStorePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NVIDIA ShadowPlay", "NvGraphics", "profiles.json");

    internal sealed record RunningGame(string Name, string ExePath);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint PrivateExtractIcons(
        string fileName, int iconIndex, int iconWidth, int iconHeight,
        [Out] IntPtr[] icons, [Out] uint[] resourceIds, uint iconCount, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW(string commandLine, out int argumentCount);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(
        IntPtr process, uint flags, System.Text.StringBuilder executablePath, ref uint size);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    private const uint ProcessQueryLimitedInformation = 0x1000;

    internal static (int Status, object Response) GetProfiles(string storePath = null)
    {
        try
        {
            lock (StoreLock)
            {
                var data = Load(storePath ?? DefaultStorePath);
                return (200, new
                {
                    profiles = data.Profiles.Select(profile => new
                    {
                        name = profile.Name,
                        exePath = profile.ExePath,
                        iconDataUri = GetExecutableIconDataUri(profile.ExePath),
                        selectedAdapterId = profile.SelectedAdapterId,
                        status = profile.Status
                    }).ToArray()
                });
            }
        }
        catch (Exception ex)
        {
            return (500, new { error = "Could not read game profiles: " + ex.Message });
        }
    }

    internal static (int Status, object Response) GetRunningGames(
        string storePath = null,
        Func<IEnumerable<RunningGame>> enumerate = null)
    {
        try
        {
            HashSet<string> existing;
            lock (StoreLock)
            {
                existing = new HashSet<string>(
                    Load(storePath ?? DefaultStorePath).Profiles.Select(profile => NormalizePath(profile.ExePath)),
                    StringComparer.OrdinalIgnoreCase);
            }

            // Process enumeration can be slow when Windows has protected or exiting processes.
            // Never hold the shared profile-store lock while inspecting process/window handles.
            var games = (enumerate ?? EnumerateRunningGames)()
                .Where(game => !existing.Contains(NormalizePath(game.ExePath)))
                .GroupBy(game => NormalizePath(game.ExePath), StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderBy(game => game.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(game => new
                {
                    name = game.Name,
                    exePath = game.ExePath,
                    iconDataUri = GetExecutableIconDataUri(game.ExePath)
                })
                .ToArray();
            return (200, new { games });
        }
        catch (Exception ex)
        {
            return (500, new { error = "Could not enumerate running games: " + ex.Message });
        }
    }

    internal static (int Status, object Response) AddRunningGame(
        byte[] body,
        string storePath = null,
        Func<IEnumerable<RunningGame>> enumerate = null)
    {
        if (!TryReadExecutablePath(body, out var exePath))
            return (400, new { error = "A valid exePath is required." });

        try
        {
            if (!Path.IsPathRooted(exePath))
                return (400, new { error = "exePath must be an absolute path." });
            exePath = Path.GetFullPath(exePath);
            if (!string.Equals(Path.GetExtension(exePath), ".exe", StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(exePath))
                return (400, new { error = "exePath must be an existing executable." });
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return (400, new { error = "exePath is invalid." });
        }

        try
        {
            // Do the potentially slow Windows process/window enumeration before acquiring
            // the shared store lock so status/profile requests remain responsive.
            var runningGame = (enumerate ?? EnumerateRunningGames)()
                .FirstOrDefault(game => string.Equals(
                    NormalizePath(game.ExePath), NormalizePath(exePath), StringComparison.OrdinalIgnoreCase));
            if (runningGame is null)
                return (409, new { error = "The executable is no longer running or is not a visible application." });

            lock (StoreLock)
            {
                var path = storePath ?? DefaultStorePath;
                var data = Load(path);
                if (data.Profiles.Any(profile =>
                        string.Equals(NormalizePath(profile.ExePath), NormalizePath(exePath),
                            StringComparison.OrdinalIgnoreCase)))
                    return (409, new { error = "This game is already in the profile list." });

                var profile = new GameProfileData
                {
                    Name = string.IsNullOrWhiteSpace(runningGame.Name)
                        ? Path.GetFileNameWithoutExtension(exePath)
                        : runningGame.Name.Trim(),
                    ExePath = exePath,
                    Status = 1,
                    StatusMessage = "Added from OSC. GPU preference has not been changed."
                };
                data.Profiles.Add(profile);
                Save(path, data);
                return (200, new
                {
                    added = true,
                    profile = new
                    {
                        name = profile.Name,
                        exePath = profile.ExePath,
                        selectedAdapterId = profile.SelectedAdapterId,
                        status = profile.Status
                    }
                });
            }
        }
        catch (Exception ex)
        {
            return (500, new { error = "Could not add the game profile: " + ex.Message });
        }
    }

    internal static (int Status, object Response) RestartRunningGame(byte[] body)
    {
        if (!TryReadExecutablePath(body, out var exePath))
            return (400, new { error = "A valid exePath is required." });

        try
        {
            if (!Path.IsPathRooted(exePath))
                return (400, new { error = "exePath must be an absolute path." });
            exePath = Path.GetFullPath(exePath);
            if (!string.Equals(Path.GetExtension(exePath), ".exe", StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(exePath))
                return (400, new { error = "exePath must be an existing executable." });

            var matches = new List<Process>();
            foreach (var candidate in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exePath)))
            {
                try
                {
                    var candidatePath = GetExecutablePath(candidate);
                    if (candidate.MainWindowHandle != IntPtr.Zero &&
                        string.Equals(Path.GetFullPath(candidatePath ?? ""), exePath,
                            StringComparison.OrdinalIgnoreCase))
                        matches.Add(candidate);
                    else
                        candidate.Dispose();
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or
                                           InvalidOperationException or NotSupportedException or
                                           UnauthorizedAccessException or ArgumentException)
                {
                    candidate.Dispose();
                }
            }

            if (matches.Count == 0)
                return (409, new { error = "The game is no longer running or has no closable window." });
            if (matches.Count > 1)
            {
                foreach (var process in matches) process.Dispose();
                return (409, new { error = "More than one matching game is running. Close it manually, then relaunch it." });
            }

            string[] launchArguments;
            using (var process = matches[0])
            {
                launchArguments = ReadProcessArguments(process);
                if (!process.CloseMainWindow())
                    return (409, new { error = "The game did not accept a graceful close. It was not force-terminated." });
                if (!process.WaitForExit(20000))
                    return (409, new { error = "The game did not close within 20 seconds. It was not force-terminated." });
            }

            var startInfo = new ProcessStartInfo(exePath)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(exePath)
            };
            foreach (var argument in launchArguments) startInfo.ArgumentList.Add(argument);
            Process.Start(startInfo);
            return (200, new { restarted = true, exePath });
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or
                                   PathTooLongException or System.ComponentModel.Win32Exception or
                                   InvalidOperationException or UnauthorizedAccessException or
                                   ManagementException)
        {
            return (500, new { error = "Could not restart the game: " + ex.Message });
        }
    }

    private static string[] ReadProcessArguments(Process process)
    {
        using var searcher = new ManagementObjectSearcher(
            "SELECT CommandLine FROM Win32_Process WHERE ProcessId = " +
            process.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        using var results = searcher.Get();
        string commandLine = null;
        foreach (ManagementObject item in results)
        {
            using (item)
            {
                commandLine = item["CommandLine"] as string;
                break;
            }
        }
        if (string.IsNullOrWhiteSpace(commandLine))
            throw new ManagementException("The running game's original command line could not be read.");

        var argv = CommandLineToArgvW(commandLine, out int count);
        if (argv == IntPtr.Zero)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(),
                "Could not parse the running game's command line.");
        try
        {
            var arguments = new string[Math.Max(0, count - 1)];
            for (var index = 1; index < count; index++)
            {
                var argument = Marshal.ReadIntPtr(argv, index * IntPtr.Size);
                arguments[index - 1] = Marshal.PtrToStringUni(argument) ?? "";
            }
            return arguments;
        }
        finally
        {
            LocalFree(argv);
        }
    }

    internal static string GetExecutableIconDataUri(string exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath)) return null;
        string normalizedPath;
        try { normalizedPath = Path.GetFullPath(exePath); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        { return null; }

        lock (IconCacheLock)
        {
            if (ExecutableIconCache.TryGetValue(normalizedPath, out var cached)) return cached;
        }

        string result = null;
        try
        {
            using var bitmap = ExtractBestExecutableIcon(normalizedPath);
            if (bitmap is null) return CacheIcon(normalizedPath, null);
            using var stream = new MemoryStream();
            bitmap.Save(stream, ImageFormat.Png);
            result = "data:image/png;base64," + Convert.ToBase64String(stream.ToArray());
        }
        catch (Exception ex) when (ex is ArgumentException or ExternalException or
                                   IOException or InvalidOperationException or
                                   UnauthorizedAccessException or NotSupportedException)
        {
            result = null;
        }
        return CacheIcon(normalizedPath, result);
    }

    private static Bitmap ExtractBestExecutableIcon(string exePath)
    {
        var handles = new IntPtr[1];
        var resourceIds = new uint[1];
        try
        {
            if (PrivateExtractIcons(exePath, 0, PreferredExecutableIconSize,
                    PreferredExecutableIconSize, handles, resourceIds, 1, 0) > 0 &&
                handles[0] != IntPtr.Zero)
            {
                using var icon = Icon.FromHandle(handles[0]);
                return icon.ToBitmap();
            }
        }
        finally
        {
            if (handles[0] != IntPtr.Zero) DestroyIcon(handles[0]);
        }

        using var fallback = Icon.ExtractAssociatedIcon(exePath);
        return fallback?.ToBitmap();
    }

    private static string CacheIcon(string path, string dataUri)
    {
        lock (IconCacheLock)
        {
            ExecutableIconCache[path] = dataUri;
        }
        return dataUri;
    }

    private static IEnumerable<RunningGame> EnumerateRunningGames()
    {
        var games = new List<RunningGame>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.MainWindowHandle == IntPtr.Zero) continue;
                    var exePath = GetExecutablePath(process);
                    if (string.IsNullOrWhiteSpace(exePath) ||
                        !string.Equals(Path.GetExtension(exePath), ".exe", StringComparison.OrdinalIgnoreCase) ||
                        !File.Exists(exePath))
                        continue;
                    var title = process.MainWindowTitle;
                    games.Add(new RunningGame(
                        string.IsNullOrWhiteSpace(title) ? process.ProcessName : title.Trim(),
                        Path.GetFullPath(exePath)));
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or
                                           InvalidOperationException or NotSupportedException or
                                           UnauthorizedAccessException)
                {
                    // Protected or exiting processes are not candidates for a profile.
                }
            }
        }
        return games;
    }

    internal static string GetExecutablePath(Process process)
    {
        try
        {
            var modulePath = process.MainModule?.FileName;
            if (!string.IsNullOrWhiteSpace(modulePath)) return modulePath;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or
                                   InvalidOperationException or NotSupportedException or
                                   UnauthorizedAccessException)
        {
            // QueryFullProcessImageName works for processes whose modules are inaccessible.
        }

        var handle = OpenProcess(ProcessQueryLimitedInformation, false, (uint)process.Id);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var buffer = new System.Text.StringBuilder(32768);
            uint size = (uint)buffer.Capacity;
            return QueryFullProcessImageName(handle, 0, buffer, ref size)
                ? buffer.ToString()
                : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private static bool TryReadExecutablePath(byte[] body, out string exePath)
    {
        exePath = null;
        try
        {
            using var document = JsonDocument.Parse(body ?? Array.Empty<byte>());
            if (document.RootElement.ValueKind != JsonValueKind.Object) return false;
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!string.Equals(property.Name, "exePath", StringComparison.OrdinalIgnoreCase) ||
                    property.Value.ValueKind != JsonValueKind.String)
                    continue;
                exePath = property.Value.GetString();
                return !string.IsNullOrWhiteSpace(exePath) && exePath.Length <= 512;
            }
        }
        catch (JsonException)
        {
            return false;
        }
        return false;
    }

    private static string NormalizePath(string path)
    {
        try { return Path.GetFullPath(path ?? "").TrimEnd(Path.DirectorySeparatorChar); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        { return path ?? ""; }
    }

    private static ProfileStoreData Load(string path)
    {
        if (!File.Exists(path)) return new ProfileStoreData();
        var data = JsonSerializer.Deserialize<ProfileStoreData>(File.ReadAllText(path));
        if (data is null) throw new InvalidOperationException("Profile store deserialized to null.");
        if (data.SchemaVersion > CurrentSchemaVersion)
            throw new NotSupportedException("Profile store schema is newer than this application.");
        if (data.Profiles is null) data.Profiles = new List<GameProfileData>();
        data.SchemaVersion = CurrentSchemaVersion;
        return data;
    }

    private static void Save(string path, ProfileStoreData data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
    }

    private sealed class ProfileStoreData
    {
        public int SchemaVersion { get; set; } = CurrentSchemaVersion;
        public List<GameProfileData> Profiles { get; set; } = new();
    }

    private sealed class GameProfileData
    {
        public string Name { get; set; } = "";
        public string ExePath { get; set; } = "";
        public string SelectedAdapterId { get; set; }
        public string LastAppliedAdapterId { get; set; }
        public DateTime? LastAppliedUtc { get; set; }
        public int Status { get; set; }
        public string StatusMessage { get; set; } = "";
    }
}
