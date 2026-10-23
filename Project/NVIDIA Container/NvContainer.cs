// NvContainer.cs — ตัวแม่ของ Duluka System (ของเราเอง 100%) — config-driven
//
// สถาปัตยกรรม (2026-09-29, หลัง approve แผน):
//   โหมด ours (default):
//     แม่ → NvGraphics.exe      (GPU manager + readiness heartbeat)
//         → NVIDIA Web Helper.exe → NvNode.exe :59011
//         → NVIDIA OSC.exe      (เรา — CEF host เลียน Share.exe)
//         → nvsphelper.exe      (เรา — Alt+Z)
//   โหมด genuine (--genuine หรือ mode ใน config):
//     แม่ → NVIDIA Share.exe แท้ (มันเลี้ยง node เองผ่าน launcher)
//
// config ทั้งระบบรวมที่ <BuildRoot>\NVIDIA Config\nvcontainer.json
// log: <BuildRoot>\NVIDIA Container\Logs\NvContainer.log
using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;

static class NvContainer
{
    // NvContainer is staged at <BuildRoot>\NVIDIA Container; resolve the
    // rest of the runtime tree from the discovered build root, not the checkout.
    static readonly string StartupRoot = FindStartupRoot();
    static string ConfigPath => Path.Combine(StartupRoot, "NVIDIA Config", "nvcontainer.json");
    static JsonElement C = JsonDocument.Parse("{}").RootElement.Clone();
    static string BuildRoot = StartupRoot;
    static string LogDir = Path.Combine(StartupRoot, "NVIDIA Container", "Logs");
    static bool ModeOurs = true;
    static bool OverlayConfigErrorReported;
    static readonly object NvGraphicsReadyLock = new object();
    static int NvGraphicsReadyPid;
    static long NvGraphicsReadyWindow;
    static DateTime NvGraphicsLastReadyUtc = DateTime.MinValue;
    static ContainerTestApi TestApi;
    static ObsRuntime ObsRuntime;
    static ApplicationOntologyClient ApplicationOntologyClient;
    static GameWatcher GameWatcher;
    static OscAdapterLifecycle OscAdapterLifecycle;
    static readonly HttpClient NodeApiHttp = new HttpClient
    {
        Timeout = TimeSpan.FromMilliseconds(1500)
    };
    // Health probes tolerate cold Node startup and transient CPU/GPU contention.
    static readonly HttpClient NodeApiHealthHttp = new HttpClient
    {
        Timeout = TimeSpan.FromMilliseconds(3000)
    };
    static readonly HttpClient NvCaptureHealthHttp = new HttpClient
    {
        Timeout = TimeSpan.FromMilliseconds(2000)
    };
    static bool NvCaptureMissingPathLogged;
    static bool NvCaptureReadyStateKnown;
    static bool NvCaptureLastReady;

    static string FindStartupRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "NVIDIA Config", "nvcontainer.json")))
                return dir.FullName;
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ".."));
    }

    static void LoadConfig()
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(ConfigPath));
            C = doc.RootElement.Clone();
            BuildRoot = ResolvePath(Str("buildRoot", "."), StartupRoot);
            ModeOurs = Str("mode", "ours") != "genuine";
            LogDir = ResolvePath(Str("logsDir", @"NVIDIA Container\Logs"), BuildRoot);
        }
        catch (Exception ex)
        {
            C = JsonDocument.Parse("{}").RootElement.Clone();
            BuildRoot = StartupRoot;
            LogDir = Path.Combine(BuildRoot, "NVIDIA Container", "Logs");
            ModeOurs = true;
            Log("[config] อ่าน " + ConfigPath + " ไม่ได้ (" + ex.Message +
                ") — ใช้ startup-relative defaults");
        }
        // พอร์ต toggle ของ OSC host (ใช้เป็นหัวใจ liveness) — จาก nvidia-osc.json
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(
                Path.Combine(BuildRoot, "NVIDIA Config", "nvidia-osc.json")));
            var osc = doc.RootElement;
            OscTogglePort = osc.GetProperty("toggle").GetInt32();
        }
        catch (Exception ex)
        {
            Log("[config] OSC toggle port unavailable: " + ex.Message);
        }
    }

    static string ResolvePath(string value, string basePath)
    {
        value = Environment.ExpandEnvironmentVariables(value);
        return Path.GetFullPath(Path.IsPathRooted(value)
            ? value
            : Path.Combine(basePath, value));
    }

    static string Str(string name, string def)
    {
        try { var v = C.GetProperty(name).GetString(); return string.IsNullOrEmpty(v) ? def : v; }
        catch { return def; }
    }

    static string Child(string name)
    {
        try { return ResolvePath(C.GetProperty("children").GetProperty(name).GetString(), BuildRoot); }
        catch { return null; }
    }

    static bool TestModeEnabled()
    {
        try { return C.GetProperty("testMode").GetProperty("enabled").GetBoolean(); }
        catch { return false; }
    }

    static bool CaptureRuntimeEnabled()
    {
        return ConfigBool("captureRuntime", "enabled", false);
    }

    // Diagnostics only. Never starts a recorder or switches the production backend.
    static object GetCaptureEngineCatalog()
    {
        var enabled = CaptureRuntimeEnabled();
        var ready = enabled && NvCaptureReady();
        // The policy is intentionally locked. Current configured/active route
        // comes from the worker, not from guessing which provider the user selected.
        var worker = enabled ? TryReadCaptureWorkerRoute() : null;
        return CaptureEngineCatalog.Snapshot(
            Path.Combine(BuildRoot, "NVIDIA Config", "capture-engines.json"),
            enabled, ready, worker);
    }

    static JsonElement? TryReadCaptureWorkerRoute()
    {
        try
        {
            using var response = NvCaptureHealthHttp.GetAsync(
                "http://127.0.0.1:" + CaptureRuntimeApiPort() + "/engines").GetAwaiter().GetResult();
            if (!response.IsSuccessStatusCode) return null;
            using var document = JsonDocument.Parse(
                response.Content.ReadAsStringAsync().GetAwaiter().GetResult());
            var root = document.RootElement;
            if (!root.TryGetProperty("schemaVersion", out var version) ||
                version.GetInt32() != 2 ||
                !root.TryGetProperty("readOnly", out var readOnly) ||
                readOnly.ValueKind != JsonValueKind.True ||
                !root.TryGetProperty("configured", out _))
                return null;
            return root.Clone();
        }
        catch
        {
            return null; // Worker may be starting; never manufacture route truth.
        }
    }

    static int CaptureRuntimeApiPort()
    {
        return ConfigInt("captureRuntime", "apiPort", 59077, 1, 65535);
    }

    static string NvCaptureExe()
    {
        try { return ResolvePath(C.GetProperty("children").GetProperty("captureExe").GetString(), BuildRoot); }
        catch { return Path.Combine(BuildRoot, "NVIDIA Capture", "NvCapture.exe"); }
    }

    static string NvCaptureWorkingDirectory()
    {
        try { return ResolvePath(C.GetProperty("children").GetProperty("captureWd").GetString(), BuildRoot); }
        catch { return Path.GetDirectoryName(NvCaptureExe()); }
    }

    static bool NvCaptureReady()
    {
        try
        {
            using var response = NvCaptureHealthHttp.GetAsync(
                "http://127.0.0.1:" + CaptureRuntimeApiPort() + "/health").GetAwaiter().GetResult();
            if (!response.IsSuccessStatusCode) return false;
            using var document = JsonDocument.Parse(
                response.Content.ReadAsStringAsync().GetAwaiter().GetResult());
            return document.RootElement.TryGetProperty("ready", out var ready) &&
                   ready.ValueKind == JsonValueKind.True;
        }
        catch { return false; }
    }

    static void EnsureNvCapture()
    {
        if (!CaptureRuntimeEnabled()) return;
        var exe = NvCaptureExe();
        if (!File.Exists(exe))
        {
            if (!NvCaptureMissingPathLogged)
            {
                Log("[capture] real capture is enabled, but NvCapture.exe is missing: " + exe);
                NvCaptureMissingPathLogged = true;
            }
            return;
        }
        NvCaptureMissingPathLogged = false;
        EnsureRunning(exe, "NvCapture", NvCaptureWorkingDirectory(), "--rest");
        var ready = NvCaptureReady();
        if (!NvCaptureReadyStateKnown || NvCaptureLastReady != ready)
        {
            NvCaptureReadyStateKnown = true;
            NvCaptureLastReady = ready;
            Log(ready ? "[capture] NvCapture is ready" : "[capture] NvCapture is starting or unavailable");
        }
    }

    static int ControlApiPort()
    {
        try
        {
            var port = C.GetProperty("controlApiPort").GetInt32();
            return port > 0 && port <= 65535 ? port : 59012;
        }
        catch { return 59012; }
    }

    static string ApplicationOntologyBaseUrl()
    {
        var environmentValue = Environment.GetEnvironmentVariable("APPLICATION_ONTOLOGY_API_BASE_URL");
        if (!string.IsNullOrWhiteSpace(environmentValue)) return environmentValue.Trim();

        try
        {
            var value = C.GetProperty("applicationOntology").GetProperty("baseUrl").GetString();
            if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
        }
        catch { }

        return "https://nv-ontology.pages.dev/api/";
    }

    static int ApplicationOntologyTimeoutSeconds()
    {
        try
        {
            var value = C.GetProperty("applicationOntology").GetProperty("timeoutSeconds").GetInt32();
            return value >= 2 && value <= 15 ? value : 5;
        }
        catch { return 5; }
    }

    static bool ConfigBool(string section, string property, bool fallback)
    {
        try
        {
            var value = C.GetProperty(section).GetProperty(property);
            return value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False
                ? value.GetBoolean() : fallback;
        }
        catch { return fallback; }
    }

    static int ConfigInt(string section, string property, int fallback, int min, int max)
    {
        try
        {
            var value = C.GetProperty(section).GetProperty(property).GetInt32();
            return value >= min && value <= max ? value : fallback;
        }
        catch { return fallback; }
    }

    static string ConfigString(string section, string property, string fallback)
    {
        try
        {
            var value = C.GetProperty(section).GetProperty(property).GetString();
            return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        }
        catch { return fallback; }
    }

    static string NvGraphicsExe()
    {
        try { return ResolvePath(C.GetProperty("children").GetProperty("graphicsExe").GetString(), BuildRoot); }
        catch { return Path.Combine(BuildRoot, "NVIDIA Graphics", "NvGraphics.exe"); }
    }

    static bool MarkNvGraphicsReady(int pid, long windowHandle)
    {
        if (pid <= 0 || windowHandle == 0) return false;
        try
        {
            using var process = Process.GetProcessById(pid);
            var processPath = process.MainModule?.FileName;
            if (!string.Equals(Path.GetFullPath(processPath ?? ""), Path.GetFullPath(NvGraphicsExe()),
                    StringComparison.OrdinalIgnoreCase))
                return false;
            var handle = new IntPtr(windowHandle);
            if (!IsWindow(handle)) return false;
            GetWindowThreadProcessId(handle, out var ownerPid);
            if (ownerPid != (uint)pid) return false;
            lock (NvGraphicsReadyLock)
            {
                NvGraphicsReadyPid = pid;
                NvGraphicsReadyWindow = windowHandle;
                NvGraphicsLastReadyUtc = DateTime.UtcNow;
            }
            return true;
        }
        catch { return false; }
    }

    static bool IsNvGraphicsProcess(int pid)
    {
        if (pid <= 0) return false;
        try
        {
            using var process = Process.GetProcessById(pid);
            return string.Equals(Path.GetFullPath(process.MainModule?.FileName ?? ""),
                Path.GetFullPath(NvGraphicsExe()), StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    static object NvGraphicsStatus()
    {
        var exe = NvGraphicsExe();
        int readyPid;
        bool heartbeatFresh;
        lock (NvGraphicsReadyLock)
        {
            readyPid = NvGraphicsReadyPid;
            heartbeatFresh = DateTime.UtcNow - NvGraphicsLastReadyUtc < TimeSpan.FromSeconds(10);
        }
        var ready = heartbeatFresh && IsNvGraphicsProcess(readyPid);
        return new { ready, running = ProcessAtPathAlive(exe), pid = ready ? readyPid : 0 };
    }

    static bool OpenNvGraphics()
    {
        int readyPid;
        long windowHandle;
        lock (NvGraphicsReadyLock)
        {
            readyPid = NvGraphicsReadyPid;
            windowHandle = NvGraphicsReadyWindow;
            if (DateTime.UtcNow - NvGraphicsLastReadyUtc >= TimeSpan.FromSeconds(10))
                return false;
        }
        if (!IsNvGraphicsProcess(readyPid)) return false;
        try
        {
            var handle = new IntPtr(windowHandle);
            if (!IsWindow(handle)) return false;
            ShowWindow(handle, SW_RESTORE);
            _ = SetForegroundWindow(handle);
            return IsWindowVisible(handle);
        }
        catch (Exception ex)
        {
            Log("[NvGraphics] could not open UI: " + ex.Message);
            return false;
        }
    }

    static void EnsureNvGraphics()
    {
        var exe = NvGraphicsExe();
        if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
        {
            Log("[NvGraphics] ไม่พบ NvGraphics.exe (" + exe + ")");
            return;
        }
        if (ProcessAtPathAlive(exe)) return;
        lock (NvGraphicsReadyLock)
        {
            NvGraphicsReadyPid = 0;
            NvGraphicsReadyWindow = 0;
            NvGraphicsLastReadyUtc = DateTime.MinValue;
        }
        Spawn(exe, Child("graphicsWd") ?? Path.GetDirectoryName(exe),
            "--control-api-port=" + ControlApiPort() + " --start-hidden");
    }

    static void PublishContainerEvents(JsonElement events)
    {
        try
        {
            var port = 59011;
            try
            {
                var configuredPort = C.GetProperty("nodeApiPort").GetInt32();
                if (configuredPort > 0 && configuredPort <= 65535) port = configuredPort;
            }
            catch { }
            var json = JsonSerializer.Serialize(new { events });
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = NodeApiHttp.PostAsync(
                "http://127.0.0.1:" + port + "/Backend/v.1.0/ContainerEvents", content)
                .GetAwaiter().GetResult();
            if (!response.IsSuccessStatusCode)
                Log("[container-event] OSC delivery failed: HTTP " + (int)response.StatusCode);
        }
        catch (Exception ex)
        {
            Log("[container-event] OSC delivery failed: " + ex.Message);
        }
    }

    static void PublishObsEvent(string name, object data)
    {
        try
        {
            using var document = JsonDocument.Parse(JsonSerializer.Serialize(new[] { new { name, data } }));
            PublishContainerEvents(document.RootElement.Clone());
        }
        catch (Exception ex)
        {
            Log("[obs] event publish failed: " + ex.Message);
        }
    }

    static void PublishGameWatcherStatus(object data)
    {
        try
        {
            using var document = JsonDocument.Parse(JsonSerializer.Serialize(new[]
            {
                new { name = "/Duluka/v1/GameWatcherStatus", data }
            }));
            PublishContainerEvents(document.RootElement.Clone());
        }
        catch (Exception ex)
        {
            Log("[game-watcher] status event publish failed: " + ex.Message);
        }
    }

    // ---- pairing kernel objects ----
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr CreateEventW(IntPtr attrs, bool manual, bool init, string name);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr CreateFileMappingW(IntPtr hFile, IntPtr attrs, uint prot, uint maxHi, uint maxLo, string name);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr MapViewOfFile(IntPtr h, uint access, uint hi, uint lo, uint n);
    [DllImport("user32.dll")]
    static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")]
    static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")]
    static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll")]
    static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    const uint PAGE_READWRITE = 0x04, FILE_MAP_ALL = 0x0F;
    const int SW_RESTORE = 9;

    static void HoldPairing()
    {
        string evName, mapName; int port; string secret;
        try
        {
            var p = C.GetProperty("pairing");
            evName = p.GetProperty("eventName").GetString();
            mapName = p.GetProperty("mapName").GetString();
            port = p.GetProperty("port").GetInt32();
            secret = p.GetProperty("secret").GetString();
        }
        catch
        {
            // ★ coexistence: ชื่อ MMF/event ต้องไม่ซ้ำกับ NVIDIA App แท้ (มันใช้ตัวเดิม)
            evName = @"Global\{1E6C4F0F-8ABF-4709-AAAD-0341CF2D44A7}-Duluka";
            mapName = "{8BA1E16C-FC54-4595-9782-E370A5FBE8DA}-Duluka";
            port = 59001; secret = "0CA906D2784F0D14E399874F5C5ED4A1";
        }
        CreateEventW(IntPtr.Zero, false, false, evName);
        var map = CreateFileMappingW(new IntPtr(-1), IntPtr.Zero, PAGE_READWRITE, 0, 4096, mapName);
        if (map == IntPtr.Zero) { Log("[pairing] MMF create failed err=" + Marshal.GetLastWin32Error()); return; }
        var view = MapViewOfFile(map, FILE_MAP_ALL, 0, 0, 4096);
        if (view == IntPtr.Zero) { Log("[pairing] map view failed err=" + Marshal.GetLastWin32Error()); return; }
        var json = Encoding.ASCII.GetBytes("{\"port\":" + port + ",\"secret\":\"" + secret + "\"}");
        var buf = new byte[4096];
        Array.Copy(json, buf, json.Length);
        Marshal.Copy(buf, 0, view, 4096);
        Log("[pairing] event + MMF held (port " + port + " + secret)");
    }

    // ไม่มี launcher แล้วในโหมด ours — แม่เขียนสัญญาณ wake ให้ node เอง
    static void WriteNvNodeInit()
    {
        try
        {
            string pathRaw = Str("nvnodeInitPath",
                "%LOCALAPPDATA%\\NVIDIA Corporation\\NvNode\\nvnode-init-duluka.json");
            string path = Environment.ExpandEnvironmentVariables(pathRaw);
            string secret = "0CA906D2784F0D14E399874F5C5ED4A1";
            int port = 59001;
            try
            {
                var p = C.GetProperty("pairing");
                port = p.GetProperty("port").GetInt32();
                secret = p.GetProperty("secret").GetString();
            } catch { }
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path,
                "{\"port\":" + port + ",\"secret\":\"" + secret.ToLower() + "\"}",
                Encoding.ASCII);
            Log("[nvnode-init] เขียน " + path);
        }
        catch (Exception ex) { Log("[nvnode-init] FAIL: " + ex.Message); }
    }

    static Process Spawn(string exe, string wd, string args = null)
    {
        try
        {
            var psi = new ProcessStartInfo(exe)
            {
                Arguments = args ?? "",
                WorkingDirectory = wd,
                UseShellExecute = true,
            };
            var process = Process.Start(psi);
            if (process == null) Log("[spawn] FAIL " + Path.GetFileName(exe) + ": Process.Start returned null");
            else Log("[spawn] " + Path.GetFileName(exe) + " PID " + process.Id);
            return process;
        }
        catch (Exception ex)
        {
            Log("[spawn] FAIL " + Path.GetFileName(exe) + ": " + ex.Message);
            return null;
        }
    }

    static void EnsureRunning(string exe, string processName, string wd, string args = null)
    {
        if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
        {
            Log("[boot] ไม่พบ " + processName + " (" + exe + ")");
            return;
        }
        // The OSC host can be alive even when process-module inspection fails.
        // Its dedicated loopback listener is a stronger liveness signal than
        // MainModule.FileName (which may throw for protected child processes).
        if ((processName == "NVIDIA OSC" && OscAlive()) || ProcessAtPathAlive(exe))
        {
            return;
        }
        Spawn(exe, wd ?? Path.GetDirectoryName(exe), args);
    }

    // MainModule.FileName can fail across integrity levels or early process
    // startup. QueryFullProcessImageName uses only limited query rights and
    // preserves the exact-path boundary against NVIDIA's genuine processes.
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(uint access, bool inherit, int processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "QueryFullProcessImageNameW")]
    static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder path, ref uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr handle);

    static string ProcessImagePath(Process process)
    {
        try
        {
            var mainModulePath = process.MainModule?.FileName;
            if (!string.IsNullOrWhiteSpace(mainModulePath)) return mainModulePath;
        }
        catch { }

        IntPtr handle = IntPtr.Zero;
        try
        {
            const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
            handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, process.Id);
            if (handle == IntPtr.Zero) return null;
            uint size = 32768;
            var path = new StringBuilder((int)size);
            return QueryFullProcessImageName(handle, 0, path, ref size) ? path.ToString() : null;
        }
        catch { return null; }
        finally { if (handle != IntPtr.Zero) CloseHandle(handle); }
    }

    static bool ProcessAtPathAlive(string exe)
    {
        if (string.IsNullOrWhiteSpace(exe)) return false;
        var expected = Path.GetFullPath(exe);
        foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(expected)))
        {
            using (p)
            {
                try
                {
                    var path = ProcessImagePath(p);
                    if (path != null &&
                        string.Equals(Path.GetFullPath(path),
                                      expected, StringComparison.OrdinalIgnoreCase)) return true;
                }
                catch { }
            }
        }
        return false;
    }

    static bool ProcessAtPathStartedRecently(string exe, int seconds)
    {
        if (string.IsNullOrWhiteSpace(exe)) return false;
        var expected = Path.GetFullPath(exe);
        foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(expected)))
        {
            using (p)
            {
                try
                {
                    var path = ProcessImagePath(p);
                    if (path != null &&
                        string.Equals(Path.GetFullPath(path),
                                      expected, StringComparison.OrdinalIgnoreCase) &&
                        DateTime.Now - p.StartTime < TimeSpan.FromSeconds(seconds)) return true;
                }
                catch { }
            }
        }
        return false;
    }

    static bool NodeApiHealthy(int port)
    {
        try
        {
            using var response = NodeApiHealthHttp.GetAsync(
                "http://127.0.0.1:" + port + "/Backend/v.1.0/health").GetAwaiter().GetResult();
            return response.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    static bool CefOverlayEnabled()
    {
        var path = Path.Combine(BuildRoot, "NVIDIA Config", "config.json");
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var overlay = doc.RootElement.GetProperty("Overlay");
            OverlayConfigErrorReported = false;
            return overlay.GetProperty("UseOverlayEnabled").GetBoolean() &&
                   overlay.GetProperty("EngineOverlayMode").GetBoolean();
        }
        catch (Exception ex)
        {
            if (!OverlayConfigErrorReported)
            {
                Log("[overlay] config unavailable; CEF overlay services remain stopped: " + ex.Message);
                OverlayConfigErrorReported = true;
            }
            return false;
        }
    }

    static void StopAtPath(string exe)
    {
        if (string.IsNullOrWhiteSpace(exe)) return;
        var expected = Path.GetFullPath(exe);
        foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(expected)))
        {
            using (p)
            {
                try
                {
                    var path = ProcessImagePath(p);
                    if (path == null ||
                        !string.Equals(Path.GetFullPath(path),
                                       expected, StringComparison.OrdinalIgnoreCase)) continue;
                    Log("[overlay] stop " + Path.GetFileName(expected) + " PID " + p.Id);
                    p.Kill();
                    p.WaitForExit(3000);
                }
                catch (Exception ex)
                {
                    Log("[overlay] stop failed " + Path.GetFileName(expected) +
                        " PID " + p.Id + ": " + ex.Message);
                }
            }
        }
    }

    static void StopOursOverlayChildren()
    {
        StopAtPath(Child("oscExe"));
        StopAtPath(Child("webHelperExe"));
        StopAtPath(Child("hotkeyExe"));
    }

    // ★ coexistence: NVIDIA App แท้มี process ชื่อเดียวกับของเรา (NVIDIA Web Helper.exe,
    //   nvcontainer) — เช็คชื่อเฉย ๆ จะหลอกว่า "ลูกเรายังมีชีวิต" ทั้งที่ตาย
    //   นับเฉพาะตัวที่ exe อยู่ใต้ tree ของเรา (build\NVIDIA ShadowPlay\)
    static string OurRoot()
    {
        return Path.GetFullPath(BuildRoot);
    }
    static bool UnderOurRoot(Process p)
    {
        try
        {
            var path = ProcessImagePath(p);
            if (path == null) return false;
            var root = OurRoot().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            return Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }   // access denied = ไม่ใช่ของเรา (ของแท้ก็ไม่อ่านได้)
    }
    static bool Alive(string name)
    {
        foreach (var p in Process.GetProcessesByName(name))
        {
            using (p)
                if (UnderOurRoot(p)) return true;
        }
        return false;
    }

    // liveness ของ OSC ด้วยพอร์ต toggle (:59003) — ชื่อ process ไม่พอ:
    // ซาก loader-stall ชื่อ "NVIDIA OSC" ค้างในระบบแล้วหลอกการเช็คชื่อ (เหตุการณ์ 05:50)
    static int OscTogglePort = 59003;
    static bool PortOpen(int port)
    {
        try
        {
            using var c = new TcpClient();
            if (c.ConnectAsync("127.0.0.1", port).Wait(700) && c.Connected) return true;
        }
        catch { }
        return false;
    }
    static bool OscAlive()
    {
        return PortOpen(OscTogglePort);
    }
    // OSC ที่กำลังบูต (มีชีวิต อายุ < 60 วิ แต่พอร์ตยังไม่เปิด) — watchdog ต้องรอ
    static bool YoungOscAlive()
    {
        foreach (var p in Process.GetProcessesByName("NVIDIA OSC"))
        {
            try
            {
                if (!UnderOurRoot(p)) continue;
                if (DateTime.Now - p.StartTime < TimeSpan.FromSeconds(60)) return true;
            }
            catch { }
        }
        return false;
    }

    static void Log(string m)
    {
        var line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + m;
        try
        {
            Directory.CreateDirectory(LogDir);
            File.AppendAllText(Path.Combine(LogDir, "NvContainer.log"),
                line + Environment.NewLine, Encoding.UTF8);
        }
        catch { }
    }

    static void Main(string[] args)
    {
        bool created;
        var single = new Mutex(true, @"Local\Duluka.NvContainer.Mother", out created);
        if (!created) return;
        GC.KeepAlive(single);

        LoadConfig();

        // --genuine ใน argv ชนะ config
        foreach (var a in args) if (string.Equals(a, "--genuine", StringComparison.OrdinalIgnoreCase)) ModeOurs = false;

        // WinExe: ห้ามแตะ Console ทุกชนิด
        Log("=== NvContainer (ตัวแม่) boot — โหมด " + (ModeOurs ? "OURS (ของเราทั้งสาย)" : "GENUINE (Share แท้)") + " ===");

        // โหมดแท้: ไม่ยึด pairing MMF — ให้ NvNode ของ NVIDIA เป็นเจ้าของเอง
        // (แม่เราเขียนทับจะทำ Share แท้อ่านค่า pairing ผิด)
        if (ModeOurs)
        {
            ObsRuntime = new ObsRuntime(
                Path.Combine(BuildRoot, "NVIDIA Config", "notifier_obs.json"), Log, PublishObsEvent);
            ObsRuntime.Start();
            ApplicationOntologyClient = new ApplicationOntologyClient(
                ApplicationOntologyBaseUrl(), ApplicationOntologyTimeoutSeconds(),
                Path.Combine(BuildRoot, "NVIDIA Config", "application-ontology-catalog.json"), Log);
            OscAdapterLifecycle = new OscAdapterLifecycle(
                BuildRoot,
                ConfigBool("oscAdapter", "enabled", false),
                ConfigString("oscAdapter", "executable", @"NVIDIA Adapter\NVIDIA OSC Adapter.exe"),
                ConfigString("oscAdapter", "workingDirectory", "NVIDIA Adapter"),
                ConfigInt("oscAdapter", "stopGraceSeconds", 3, 0, 15),
                Log);
            GameWatcher = new GameWatcher(
                ApplicationOntologyClient,
                OscAdapterLifecycle,
                ConfigBool("gameWatcher", "enabled", true),
                ConfigInt("gameWatcher", "pollIntervalSeconds", 3, 1, 15),
                ConfigInt("gameWatcher", "catalogRefreshSeconds", 30, 10, 300),
                PublishGameWatcherStatus,
                Log);
            GameWatcher.Start();
            TestApi = new ContainerTestApi(TestModeEnabled(), ControlApiPort(), Log,
                PublishContainerEvents, ObsRuntime, MarkNvGraphicsReady, NvGraphicsStatus,
                OpenNvGraphics, ApplicationOntologyClient, GameWatcher, OscAdapterLifecycle,
                GetCaptureEngineCatalog);
            TestApi.Start();
            Log("[test-api] test mode " + (TestModeEnabled() ? "enabled: capture/filter actions are simulated" : "disabled"));
            HoldPairing();
            EnsureNvGraphics();
        }

        if (ModeOurs)
        {
            WriteNvNodeInit();

            var host = Child("webHelperExe");
            var osc  = Child("oscExe");
            var hk   = Child("hotkeyExe");

            if (CefOverlayEnabled())
            {
                EnsureRunning(host, "NVIDIA Web Helper", Child("webHelperWd"));
                EnsureRunning(hk, "nvsphelper", Path.GetDirectoryName(hk));
                EnsureRunning(osc, "NVIDIA OSC", Child("oscWd"));
            }
            else
            {
                StopOursOverlayChildren();
                Log("[overlay] CEF engine/system disabled; overlay services remain stopped");
            }

            WatchOurs();
        }
        else
        {
            var share = Child("genuineShareExe");
            if (share == null || !File.Exists(share)) { Log("[boot] ไม่เจอ NVIDIA Share.exe ที่ " + share); return; }
            Log("[spawn] NVIDIA Share.exe (แท้) — มันเลี้ยง node เองผ่าน launcher");
            if (!Alive("nvsphelper"))
            {
                var hk = Child("hotkeyExe");
                if (hk != null && File.Exists(hk)) Spawn(hk, Path.GetDirectoryName(hk));
            }
            Spawn(share, Child("genuineShareWd") ?? Path.GetDirectoryName(share));
            WatchGenuine(share);
        }
    }

    // ---- watchdog โหมด ours: ลูก 3 ตัวของเรา ----
    static void WatchOurs()
    {
        var host = Child("webHelperExe");
        var osc  = Child("oscExe");
        var hk   = Child("hotkeyExe");
        var nodeApiPort = 59011;
        try { nodeApiPort = C.GetProperty("nodeApiPort").GetInt32(); } catch { }
        int interval = 5;
        try { interval = C.GetProperty("watchdog").GetProperty("intervalSeconds").GetInt32(); } catch { }
        var spills = 0;
        var unhealthyNodeApiChecks = 0;
        long lastOscSpawnAttempt = -30000;
        var enabled = CefOverlayEnabled();
        var elapsed = 0;

        while (true)
        {
            // ★ แม่ห้ามตาย: exception ใด ๆ ในรอบเฝ้ายาม (process race, IO) = log แล้วรอบต่อไป
            try
            {
                Thread.Sleep(1000);

                var nowEnabled = CefOverlayEnabled();
                if (nowEnabled != enabled)
                {
                    enabled = nowEnabled;
                    elapsed = 0;
                    if (enabled)
                    {
                        Log("[overlay] CEF engine/system enabled — starting overlay services");
                        EnsureRunning(host, "NVIDIA Web Helper", Child("webHelperWd"));
                        EnsureRunning(hk, "nvsphelper", Path.GetDirectoryName(hk));
                        EnsureRunning(osc, "NVIDIA OSC", Child("oscWd"));
                    }
                    else
                    {
                        Log("[overlay] CEF engine/system disabled — stopping overlay services");
                        StopOursOverlayChildren();
                    }
                    continue;
                }
                if (++elapsed < interval) continue;
                elapsed = 0;

                EnsureNvGraphics();
                var nodeApiReady = enabled && NodeApiHealthy(nodeApiPort);
                if (nodeApiReady) EnsureNvCapture();
                if (!enabled) continue;

                EnsureRunning(hk, "nvsphelper", Path.GetDirectoryName(hk));

                // The host owns the NvNode runtime in a kill-on-close job.
                // A dead child exits the host; an unhealthy endpoint while
                // the host remains alive is recovered after its startup grace.
                if (host != null && File.Exists(host))
                {
                    if (!ProcessAtPathAlive(host))
                    {
                        unhealthyNodeApiChecks = 0;
                        WipeOrphans();
                        spills++;
                        Log("[watchdog] Web Helper หาย (รอบที่ " + spills + ") — spawn ใหม่");
                        Spawn(host, Child("webHelperWd") ?? Path.GetDirectoryName(host));
                    }
                    else if (ProcessAtPathStartedRecently(host, 60))
                    {
                        // Give the child a startup grace period; don't count cold-start probes.
                        unhealthyNodeApiChecks = 0;
                    }
                    else if (nodeApiReady)
                    {
                        unhealthyNodeApiChecks = 0;
                    }
                    else
                    {
                        unhealthyNodeApiChecks++;
                        Log("[watchdog] health probe failed " + unhealthyNodeApiChecks +
                            "/3 at :" + nodeApiPort + " — keeping Web Helper alive until confirmed");
                        if (unhealthyNodeApiChecks >= 3)
                        {
                            unhealthyNodeApiChecks = 0;
                            foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(host)))
                            {
                                using (p)
                                {
                                    try
                                    {
                                        var path = ProcessImagePath(p);
                                        if (path == null ||
                                            !string.Equals(Path.GetFullPath(path),
                                                           Path.GetFullPath(host), StringComparison.OrdinalIgnoreCase)) continue;
                                        Log("[watchdog] NvNode/API ไม่ตอบ health 3 ครั้งติดที่ :" +
                                            nodeApiPort + " — restart Web Helper PID " + p.Id);
                                        p.Kill();
                                        p.WaitForExit(3000);
                                    }
                                    catch (Exception ex)
                                    {
                                        Log("[watchdog] Web Helper restart failed: " + ex.Message);
                                    }
                                }
                            }
                            if (!ProcessAtPathAlive(host))
                            {
                                Thread.Sleep(1000);
                                Spawn(host, Child("webHelperWd") ?? Path.GetDirectoryName(host));
                            }
                        }
                    }
                }

                // หน้าต่าง OSC: เช็คพอร์ต toggle — ตายจริง (พอร์ตปิด) ค่อยเปิดใหม่
                // ★ แต่บูต CEF+GPU ใช้เวลาได้เกิน 15 วิ (เมื่อ GPU ต้องแบ่งกับ NVIDIA App แท้)
                //   → ถ้า process ยังอยู่และอายุ < 60 วิ = กำลังบูต ห้ามฆ่า
                if (osc != null && File.Exists(osc))
                {
                    // A live toggle listener wins over an unreliable
                    // Process.MainModule lookup. Without this guard the mother
                    // attempted to spawn a second OSC every five seconds.
                    if (OscAlive()) continue;
                    if (!Alive("NVIDIA OSC"))
                    {
                        // Rate-limit failed cold starts; OSC's single-instance
                        // mutex alone must not be our spawn-loop protection.
                        long now = Environment.TickCount64;
                        if (now - lastOscSpawnAttempt >= 30000)
                        {
                            lastOscSpawnAttempt = now;
                            Log("[watchdog] NVIDIA OSC listener/process unavailable — spawn (30s backoff)");
                            Spawn(osc, Child("oscWd") ?? Path.GetDirectoryName(osc));
                        }
                    }
                    else if (!YoungOscAlive())
                    {
                        // รีสตาร์ตเฉพาะ OSC ของเรา ไม่แตะ NVIDIA App ที่ใช้ชื่อ process เดียวกัน
                        foreach (var p in Process.GetProcessesByName("NVIDIA OSC"))
                        {
                            using (p)
                            {
                                try
                                {
                                    if (!UnderOurRoot(p)) continue;
                                    Log("[watchdog] เช็ด OSC ค้าง PID " + p.Id);
                                    p.Kill();
                                    p.WaitForExit(3000);
                                }
                                catch { }
                            }
                        }
                        if (!Alive("NVIDIA OSC") && !OscAlive())
                        {
                            long now = Environment.TickCount64;
                            if (now - lastOscSpawnAttempt >= 30000)
                            {
                                lastOscSpawnAttempt = now;
                                Thread.Sleep(1000);
                                Log("[watchdog] NVIDIA OSC ไม่ตอบพอร์ต :" + OscTogglePort + " — spawn ใหม่");
                                Spawn(osc, Child("oscWd") ?? Path.GetDirectoryName(osc));
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                try { Log("[watchdog] รอบยกเลิก: " + ex.GetType().Name + " — " + ex.Message); } catch { }
            }
        }
    }

    // ---- watchdog โหมด genuine (พฤติกรรมเดิม) ----
    static void WatchGenuine(string share)
    {
        var hk = Child("hotkeyExe");
        int interval = 15;
        try { interval = C.GetProperty("watchdog").GetProperty("intervalSeconds").GetInt32(); } catch { }
        var spills = 0;

        while (true)
        {
            Thread.Sleep(interval * 1000);
            if (hk != null && File.Exists(hk) && !Alive("nvsphelper")) Spawn(hk, Path.GetDirectoryName(hk));

            if (!Alive("NVIDIA Share"))
            {
                WipeOrphans();
                Thread.Sleep(2000);
                spills++;
                Log("[watchdog] Share หาย (รอบที่ " + spills + ") — เช็ด orphan แล้ว spawn ใหม่");
                Spawn(share, Child("genuineShareWd") ?? Path.GetDirectoryName(share));
            }
        }
    }

    static void WipeOrphans()
    {
        string[] names;
        try { names = System.Text.Json.JsonSerializer.Deserialize<string[]>(
                  C.GetProperty("watchdog").GetProperty("orphanWipeNames").GetRawText()); }
        catch { names = new[] { "nvnodejslauncher", "node", "NVIDIA Web Helper" }; }
        // ★ coexistence: เช็ดเฉพาะ orphan "ใต้ tree เรา" — ของแท้ (Program Files) ห้ามแตะ
        foreach (var n in names)
            foreach (var p in Process.GetProcessesByName(n))
            {
                if (!UnderOurRoot(p)) continue;
                try { Log("[watchdog] เช็ด orphan " + n + " PID " + p.Id); p.Kill(); }
                catch { }
            }
    }
}
