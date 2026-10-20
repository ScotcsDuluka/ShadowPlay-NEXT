using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;

internal static class Program
{
    private const uint ProcessCreateThread = 0x0002;
    private const uint ProcessQueryInformation = 0x0400;
    private const uint ProcessVmOperation = 0x0008;
    private const uint ProcessVmWrite = 0x0020;
    private const uint MemCommit = 0x1000;
    private const uint MemReserve = 0x2000;
    private const uint MemRelease = 0x8000;
    private const uint PageReadWrite = 0x04;
    private const uint WaitObject0 = 0;
    private const uint WaitTimeout = 0x00000102;
    private const uint Infinite = 0xFFFFFFFF;
    private const ushort ImageFileMachineUnknown = 0;
    private const ushort ImageFileMachineI386 = 0x014c;
    private const ushort ImageFileMachineAmd64 = 0x8664;

    private static int Main(string[] args)
    {
        try
        {
            var options = ParseArguments(args);
            try
            {
                using var game = Process.GetProcessById(options.GamePid);
                ValidateGameIdentity(game, options.GameExecutable);

                var targetIs64Bit = IsSupportedX64Process(game.Handle);
                if (targetIs64Bit != Environment.Is64BitProcess)
                {
                    if (!Environment.Is64BitProcess)
                        throw new InvalidOperationException("The x86 adapter cannot attach to an x64 game.");
                    if (options.AnselOnly)
                        throw new InvalidOperationException("Ansel Game Filter currently supports x64 games only.");
                    var hostPath = Path.Combine(AppContext.BaseDirectory, "x86", "NVIDIA OSC Adapter.exe");
                    if (!File.Exists(hostPath))
                        throw new FileNotFoundException("The x86 OSC Adapter host is missing.", hostPath);
                    var start = new ProcessStartInfo(hostPath) { UseShellExecute = false, CreateNoWindow = true };
                    foreach (var argument in args) start.ArgumentList.Add(argument);
                    using var child = Process.Start(start)
                        ?? throw new InvalidOperationException("Could not start the x86 OSC Adapter host.");
                    Log("Routing game PID " + options.GamePid + " to the x86 adapter host.");
                    child.WaitForExit();
                    return child.ExitCode;
                }

                var adapterPath = Path.Combine(AppContext.BaseDirectory,
                    options.AnselOnly ? "NVIDIA Ansel Adapter.dll" : "NVIDIA OSC Adapter Hook.dll");
                if (!File.Exists(adapterPath))
                    throw new FileNotFoundException("The matching OSC Adapter DLL is missing.", adapterPath);

                InjectDxgiAdapter(options.GamePid, adapterPath);
                Log((options.AnselOnly ? "Ansel filter adapter" : "DXGI adapter") +
                    " loaded for " + options.GameName + " (game PID " + options.GamePid + ").");

                try
                {
                    game.WaitForExit();
                }
                catch (InvalidOperationException)
                {
                    // The game exited between the successful load and wait registration.
                }

                return 0;
            }
            catch (Win32Exception ex) when (
                ex.NativeErrorCode == 5 &&
                !options.ElevationRetry &&
                !IsAdministrator())
            {
                Log("Access denied while attaching to " + options.GameName +
                    " (PID " + options.GamePid + "); requesting an elevated retry.");
                return RestartElevated(args);
            }
        }
        catch (Exception ex)
        {
            Log(ex.GetType().Name + ": " + ex.Message);
            return 1;
        }
    }

    private static (int GamePid, string GameName, string GameExecutable, bool ElevationRetry, bool AnselOnly) ParseArguments(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i + 1 < args.Length; i += 2)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException("Unexpected argument: " + args[i]);
            values[args[i]] = args[i + 1];
        }
        if (args.Length % 2 != 0)
            throw new ArgumentException("Every option must have a value.");

        if (!values.TryGetValue("--game-pid", out var pidText) ||
            !int.TryParse(pidText, out var pid) || pid <= 0)
            throw new ArgumentException("A valid --game-pid is required.");
        if (!values.TryGetValue("--game-name", out var name) || string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("A nonempty --game-name is required.");
        if (!values.TryGetValue("--game-executable", out var executable) || string.IsNullOrWhiteSpace(executable))
            throw new ArgumentException("A nonempty --game-executable is required.");

        var elevationRetry = values.TryGetValue("--elevation-retry", out var retryText) &&
            bool.TryParse(retryText, out var retryRequested) && retryRequested;
        var anselOnly = values.TryGetValue("--ansel-only", out var anselText) &&
            bool.TryParse(anselText, out var anselRequested) && anselRequested;
        return (pid, name, Path.GetFullPath(executable), elevationRetry, anselOnly);
    }

    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static int RestartElevated(string[] args)
    {
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable))
            throw new InvalidOperationException("Could not determine the OSC Adapter host executable for elevated retry.");

        var retryArgs = args.Concat(new[] { "--elevation-retry", "true" });
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            Arguments = string.Join(" ", retryArgs.Select(QuoteWindowsArgument)),
            WorkingDirectory = AppContext.BaseDirectory,
            UseShellExecute = true,
            Verb = "runas"
        };

        using var elevatedProcess = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Windows did not start the elevated OSC Adapter host.");
        Log("Elevated retry started (PID " + elevatedProcess.Id + "). Waiting for its result.");
        elevatedProcess.WaitForExit();
        return elevatedProcess.ExitCode;
    }

    private static string QuoteWindowsArgument(string value)
    {
        const char slash = (char)92;
        const char quote = (char)34;
        var builder = new System.Text.StringBuilder().Append(quote);
        var backslashes = 0;
        foreach (var character in value)
        {
            if (character == slash)
            {
                backslashes++;
                continue;
            }

            if (character == quote)
            {
                builder.Append(slash, backslashes * 2 + 1).Append(quote);
            }
            else
            {
                builder.Append(slash, backslashes).Append(character);
            }
            backslashes = 0;
        }

        builder.Append(slash, backslashes * 2).Append(quote);
        return builder.ToString();
    }

    private static void ValidateGameIdentity(Process game, string expectedExecutable)
    {
        var actualExecutable = Path.GetFullPath(game.MainModule?.FileName
            ?? throw new InvalidOperationException("Could not read the target process image path."));
        if (!string.Equals(actualExecutable, expectedExecutable, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The target PID no longer matches the expected game executable.");

    }

    private static bool IsSupportedX64Process(IntPtr process)
    {
        if (!IsWow64Process2(process, out var processMachine, out var nativeMachine))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not determine target process architecture.");

        if (nativeMachine != ImageFileMachineAmd64 ||
            (processMachine != ImageFileMachineUnknown && processMachine != ImageFileMachineI386))
            throw new InvalidOperationException("Only Windows x86 and x64 game processes are supported.");
        return processMachine == ImageFileMachineUnknown;
    }

    private static void InjectDxgiAdapter(int processId, string dllPath)
    {
        var process = OpenProcess(
            ProcessCreateThread | ProcessQueryInformation | ProcessVmOperation | ProcessVmWrite,
            false,
            processId);
        if (process == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "OpenProcess failed.");

        IntPtr remotePath = IntPtr.Zero;
        IntPtr thread = IntPtr.Zero;
        try
        {
            var encodedPath = System.Text.Encoding.Unicode.GetBytes(dllPath + '\0');
            remotePath = VirtualAllocEx(
                process, IntPtr.Zero, (nuint)encodedPath.Length,
                MemCommit | MemReserve, PageReadWrite);
            if (remotePath == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "VirtualAllocEx failed.");

            if (!WriteProcessMemory(process, remotePath, encodedPath, (nuint)encodedPath.Length, out var written) ||
                written != (nuint)encodedPath.Length)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "WriteProcessMemory did not write the full DLL path.");

            var kernel32 = GetModuleHandleW("kernel32.dll");
            if (kernel32 == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "GetModuleHandleW(kernel32.dll) failed.");
            var loadLibrary = GetProcAddress(kernel32, "LoadLibraryW");
            if (loadLibrary == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "GetProcAddress(LoadLibraryW) failed.");

            thread = CreateRemoteThread(process, IntPtr.Zero, 0, loadLibrary, remotePath, 0, out _);
            if (thread == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateRemoteThread failed.");

            var wait = WaitForSingleObject(thread, 10000);
            if (wait == WaitTimeout)
                wait = WaitForSingleObject(thread, Infinite);
            if (wait != WaitObject0)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Waiting for the remote loader thread failed.");
            if (!GetExitCodeThread(thread, out var loadResult) || loadResult == 0)
                throw new InvalidOperationException("LoadLibraryW failed inside the target process.");
        }
        finally
        {
            if (thread != IntPtr.Zero)
            {
                WaitForSingleObject(thread, Infinite);
                CloseHandle(thread);
            }
            if (remotePath != IntPtr.Zero) VirtualFreeEx(process, remotePath, 0, MemRelease);
            CloseHandle(process);
        }
    }

    private static void Log(string message)
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NVIDIA", "OSC Adapter", "Logs");
        try
        {
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, "adapter-host.log"),
                DateTimeOffset.UtcNow.ToString("O") + " " + message + Environment.NewLine);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr VirtualAllocEx(IntPtr process, IntPtr address, nuint size, uint allocationType, uint protect);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool VirtualFreeEx(IntPtr process, IntPtr address, nuint size, uint freeType);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WriteProcessMemory(IntPtr process, IntPtr address, byte[] buffer, nuint size, out nuint written);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateRemoteThread(
        IntPtr process, IntPtr attributes, nuint stackSize,
        IntPtr startAddress, IntPtr parameter, uint creationFlags, out uint threadId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeThread(IntPtr thread, out uint exitCode);

    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW",
        SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern IntPtr GetModuleHandleW(string moduleName);

    [DllImport("kernel32.dll", EntryPoint = "GetProcAddress",
        SetLastError = true, CharSet = CharSet.Ansi, ExactSpelling = true)]
    private static extern IntPtr GetProcAddress(IntPtr module, string procedureName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWow64Process2(
        IntPtr process, out ushort processMachine, out ushort nativeMachine);
}
