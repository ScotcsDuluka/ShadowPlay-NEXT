// NvHotkeyHelper.cs — NVIDIA ShadowPlay Helper (ของเรา 100%) — แบบของแท้
// บทบาท (เหมือน nvsphelper64.exe แท้): เป็น "ตัวจับ hotkey ระดับ OS" ตัวเดียวของระบบ
//   • อ่านคีย์ที่ผู้ใช้ปรับจาก Overlay OSC\NVIDIA NodeAPI\shadowplay-settings.json (หน้า OSC settings เขียน)
//   • RegisterHotKey ทุกตัวที่มี modifier (Alt+F1, Alt+F9, Ctrl+Alt+R …)
//   • กดแล้วยิง :59002/?hk=<Name> → node shim → socket /ShadowPlay/v.1.0/Hotkey → หน้าทำงาน
//   • FileSystemWatcher ตามไฟล์ settings — แก้คีย์ใน OSC แล้วมีผลทันที (ไม่ต้องรีสตาร์ท)
//   • OpenShare ยิง hotkey callback เข้า NvNode; OSC page เป็นเจ้าของ toggle state
// config: <BuildRoot>\NVIDIA Config\nvsphelper.json
//
// WinExe: ห้ามแตะ Console ทุกชนิด (handle invalid = crash) — log ลงไฟล์เท่านั้น
using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

static class NvShadowPlayHelper
{
    [DllImport("user32.dll", SetLastError = true)]
    static extern bool RegisterHotKey(IntPtr h, int id, uint mods, uint vk);
    [DllImport("user32.dll")]
    static extern bool UnregisterHotKey(IntPtr h, int id);
    [DllImport("user32.dll")]
    static extern short GetAsyncKeyState(int vKey);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr window, StringBuilder text, int count);
    [DllImport("user32.dll")]
    static extern int PeekMessageW(out MSG m, IntPtr h, uint a, uint b, uint remove);
    [DllImport("user32.dll", SetLastError = true)]
    static extern uint MsgWaitForMultipleObjectsEx(uint count, IntPtr handles, uint milliseconds, uint wakeMask, uint flags);
    const uint QS_ALLINPUT = 0x04FF, MWMO_INPUTAVAILABLE = 0x0004, WAIT_FAILED = 0xFFFFFFFF;

    [StructLayout(LayoutKind.Sequential)]
    struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam; public IntPtr lParam; public uint time; public int ptX; public int ptY; }

    const uint WM_HOTKEY = 0x0312;
    const uint MOD_NOREPEAT = 0x4000;   // [P2#4 Copilot] 1 event ต่อ key-down (Win10+)
    const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_WIN = 0x8;
    static readonly string BuildRoot = FindBuildRoot();
    static readonly string ConfigPath = Path.Combine(BuildRoot, "NVIDIA Config", "nvsphelper.json");
    static readonly string SettingsPath = Path.Combine(BuildRoot, "Overlay On-Screen Control", "NVIDIA NodeAPI", "shadowplay-settings.json");
    static string _fireUrl = "http://127.0.0.1:59012/v1/hotkey/{name}";
    // OSC's existing Hotkey/Monitor endpoint is toggled by the keyboard-shortcut editor.
    static readonly string MonitorUrl = "http://127.0.0.1:59011/ShadowPlay/v.1.0/Hotkey/Monitor";
    static readonly HttpClient MonitorClient = new HttpClient(new HttpClientHandler { UseProxy = false })
    { Timeout = TimeSpan.FromMilliseconds(500) };
    static int _monitorDesired = 1; // 0 = editor capturing shortcuts, 1 = global keys enabled
    static bool _monitorRegistered = true; // only main thread may register/unregister Win32 keys
    static string _logFile = Path.Combine(BuildRoot, "NvContainer", "Logs", "nvsphelper.log");

    static string FindBuildRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "NVIDIA Config", "nvsphelper.json")))
                return dir.FullName;
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ".."));
    }

    // ค่า default ตาม GFE แท้ (route-floor HOTKEYS) — ใช้เมื่อ settings ไม่มีชื่อนั้น
    static readonly Dictionary<string, int[]> Defaults = new Dictionary<string, int[]>
    {
        ["openshare"]            = new[] { 18, 90 },    // Alt+Z
        ["screenshot"]           = new[] { 18, 112 },   // Alt+F1
        ["recordtoggle"]         = new[] { 18, 120 },   // Alt+F9
        ["recordsave"]           = new[] { 18, 121 },   // Alt+F10
        ["dvrtoggle"]            = new[] { 17, 18, 121 }, // Ctrl+Alt+F10
        ["instantreplaytoggle"]  = new[] { 18, 117 },   // Alt+F7
        ["broadcasttoggle"]      = new[] { 18, 119 },   // Alt+F8
        ["broadcastpausetoggle"] = new[] { 16, 18, 119 },
        ["cameratoggle"]         = new[] { 18, 67 },    // Alt+C
        ["mictoggle"]            = new[] { 18, 77 },    // Alt+M
        ["fps"]                  = new[] { 18, 80 },    // Alt+P
        ["commentstoggle"]       = new[] { 18, 88 },
        ["modsui"]               = new[] { 18, 114 },   // Alt+F3
        ["modstoggle"]           = new[] { 16, 18, 114 },
        ["nvcameraui"]           = new[] { 18, 113 },
        ["overlayfocus"]         = new[] { 18, 16, 84 }, // Alt+Shift+T
    };

    // ชื่อใน settings (lowercase) → ชื่อทางการที่หน้า/node ใช้ (we.HotkeyShortcuts)
    static readonly Dictionary<string, string> Canonical = new Dictionary<string, string>
    {
        ["openshare"] = "OpenShare", ["ptt"] = "PTT", ["fps"] = "FPS",
        ["screenshot"] = "Screenshot", ["recordsave"] = "RecordSave",
        ["recordtoggle"] = "RecordToggle", ["broadcasttoggle"] = "BroadcastToggle",
        ["broadcastpausetoggle"] = "BroadcastPauseToggle", ["cameratoggle"] = "CameraToggle",
        ["mictoggle"] = "MicToggle", ["dvrtoggle"] = "DVRToggle",
        ["instantreplaytoggle"] = "InstantReplayToggle", ["commentstoggle"] = "CommentsToggle",
        ["overlayaswitch"] = "OverlayASwitch", ["overlaybswitch"] = "OverlayBSwitch",
        ["overlaycswitch"] = "OverlayCSwitch", ["modsui"] = "ModsUI",
        ["modstoggle"] = "ModsToggle", ["modspreset1"] = "ModsPreset1",
        ["modspreset2"] = "ModsPreset2", ["modspreset3"] = "ModsPreset3",
        ["nvcameraui"] = "NvCameraUI", ["octooluitoggle"] = "OcToolUIToggle",
    };

    // id ที่ลงทะเบียนอยู่: id → (canonicalName, mods, vk)
    static readonly Dictionary<int, Tuple<string, uint, uint>> Registered = new Dictionary<int, Tuple<string, uint, uint>>();
    static int _nextId = 1;
    // RegisterHotKey(NULL,…) ผูกกับเธรดที่เรียก — re-register ต้องเกิดบนเธรดหลักเท่านั้น
    // (watcher แค่ปักธง หลักวนมาเห็นแล้วค่อยทำ — ไม่งั้นถอดของเดิมไม่ได้ = 1409 ค้าง)
    static long _settingsChangedAt = -1;
    static FileSystemWatcher _settingsWatcher;
    static long _lastSettingsProbe;
    static string _settingsFileStamp;
    static string _registeredHotkeySignature = "";
    const long SETTINGS_CHANGE_DEBOUNCE_MS = 250;
    // ★ retry: คีย์ที่ลงทะเบียนไม่ได้ตอนบูต (มีคนถือชั่วคราว เช่น grip ตอนปิดสายแท้)
    //   ลองซ้ำทุก 10 วิ จนได้ — ได้เมื่อไหร่ Alt+X กลับมาเอง
    static readonly Dictionary<string, Tuple<uint, uint>> FailedKeys = new Dictionary<string, Tuple<uint, uint>>();
    static long _lastRetry = -100000;
    // ★ upgrade-retry: คีย์ที่ได้ fallback (+Shift) ไว้ก่อน — ลองเอาคีย์หลักคืนทุก 10 วิ
    static readonly Dictionary<string, Tuple<uint, uint>> UpgradableKeys = new Dictionary<string, Tuple<uint, uint>>();
    static readonly HotkeyMessageGate MessageGate = new HotkeyMessageGate();
    static long _monitorGeneration;
    // Serialize callbacks: toggles must reach Container in keypress order.
    // A bounded queue prevents a disconnected Container from exhausting the ThreadPool.
    // Never replay stale toggles after a backend outage.
    static readonly BlockingCollection<HotkeyDispatchRequest> DispatchQueue =
        new BlockingCollection<HotkeyDispatchRequest>(new ConcurrentQueue<HotkeyDispatchRequest>(), 64);

    static void EnqueuePost(string url, string body = "{}")
    {
        if (!DispatchQueue.TryAdd(new HotkeyDispatchRequest(url, body, Environment.TickCount64,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Interlocked.Read(ref _monitorGeneration))))
            Log("[hotkey] dispatch queue full; dropped request for " + url);
    }

    static void StartDispatcher()
    {
        var worker = new Thread(() =>
        {
            foreach (var item in DispatchQueue.GetConsumingEnumerable())
            {
                if (!item.CanDispatch(Environment.TickCount64, Interlocked.Read(ref _monitorGeneration),
                    Volatile.Read(ref _monitorDesired) != 0))
                {
                    Log("[hotkey] stale queued request dropped id=" + item.RequestId);
                    continue;
                }
                Post(item);
            }
        }) { IsBackground = true, Name = "NvHotkeyDispatch" };
        worker.Start();
    }

    static bool? ParseMonitorEnabled(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (doc.RootElement.TryGetProperty("enable", out var v) &&
                (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False))
                return v.GetBoolean();
        }
        catch (JsonException) { }
        return null;
    }

    static bool? ReadMonitorEnabled()
    {
        try
        {
            return ParseMonitorEnabled(MonitorClient.GetStringAsync(MonitorUrl).GetAwaiter().GetResult());
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // Keep the last confirmed monitoring state during transient NodeAPI outages.
            return null;
        }
    }

    static void StartMonitorProbe()
    {
        var worker = new Thread(() =>
        {
            long lastSuccess = Environment.TickCount64;
            while (true)
            {
                var enable = ReadMonitorEnabled();
                if (enable.HasValue)
                {
                    SetMonitorDesired(enable.Value);
                    lastSuccess = Environment.TickCount64;
                }
                else if (Environment.TickCount64 - lastSuccess > 10000)
                {
                    // Backend has vanished: restore standalone shortcuts rather than
                    // leave a permanently muted Helper after an OSC crash.
                    SetMonitorDesired(true);
                }
                Thread.Sleep(180);
            }
        }) { IsBackground = true, Name = "NvHotkeyMonitor" };
        worker.Start();
    }

    static void SuspendAll()
    {
        foreach (var kv in Registered)
            if (!UnregisterHotKey(IntPtr.Zero, kv.Key))
                Log("[monitor] unregister failed id=" + kv.Key + " err=" + Marshal.GetLastWin32Error());
        Registered.Clear();
        FailedKeys.Clear();
        UpgradableKeys.Clear();
        MessageGate.Clear();
        _registeredHotkeySignature = "";
        // Drop events already queued before editor was opened.
        while (DispatchQueue.TryTake(out _)) { }
        Log("[monitor] keyboard-shortcut editor active; native hotkeys released");
    }

    static void SyncMonitorState()
    {
        bool wantRegistration = Volatile.Read(ref _monitorDesired) != 0;
        if (wantRegistration == _monitorRegistered) return;
        _monitorRegistered = wantRegistration;
        if (wantRegistration)
        {
            Log("[monitor] shortcuts editor closed; restoring hotkeys");
            RegisterAll(); // Always load latest user bindings, including edits made while paused.
        }
        else SuspendAll();
    }

    static void SetMonitorDesired(bool enabled)
    {
        var desired = enabled ? 1 : 0;
        if (Interlocked.Exchange(ref _monitorDesired, desired) != desired)
            Interlocked.Increment(ref _monitorGeneration);
    }

    static void LoadConfig()
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(ConfigPath));
            var r = doc.RootElement;
            if (r.TryGetProperty("fireUrl", out var v)) _fireUrl = v.GetString();
            if (r.TryGetProperty("logFile", out v))
            {
                var configuredLog = Environment.ExpandEnvironmentVariables(v.GetString());
                _logFile = Path.GetFullPath(Path.IsPathRooted(configuredLog)
                    ? configuredLog
                    : Path.Combine(BuildRoot, configuredLog));
            }
        }
        catch (Exception ex) { Log("[config] " + ConfigPath + ": " + ex.Message); }
    }

    static void Log(string m)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_logFile));
            File.AppendAllText(_logFile,
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + m + Environment.NewLine,
                Encoding.UTF8);
        }
        catch { }
    }

    static bool IsSettingsFile(string path)
    {
        var fullPath = Path.GetFullPath(path);
        return string.Equals(fullPath, SettingsPath, StringComparison.OrdinalIgnoreCase) ||
               fullPath.StartsWith(SettingsPath + ".", StringComparison.OrdinalIgnoreCase);
    }

    // อ่านคีย์ทั้งหมด: settings ของผู้ใช้ก่อน → default มาเติมที่ขาด
    static Dictionary<string, int[]> LoadAllKeys(bool keepCurrentOnFailure = false)
    {
        // Put explicit user bindings first so they win over conflicting defaults.
        var all = new Dictionary<string, int[]>(StringComparer.Ordinal);
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(SettingsPath));
            if (doc.RootElement.TryGetProperty("hotkeys", out var hk) && hk.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in hk.EnumerateObject())
                {
                    if (p.Value.ValueKind != JsonValueKind.Array) continue;
                    var keys = new List<int>();
                    foreach (var e in p.Value.EnumerateArray())
                        if (e.TryGetInt32(out int k)) keys.Add(k);
                    if (keys.Count > 0) all[p.Name.ToLowerInvariant()] = keys.ToArray();
                }
            }
        }
        catch (Exception ex)
        {
            if (keepCurrentOnFailure) return null;
            if (ex is not FileNotFoundException && ex is not DirectoryNotFoundException)
                Log("[settings] read failed; using defaults: " + ex.Message);
        }
        foreach (var kv in Defaults)
            if (!all.ContainsKey(kv.Key)) all[kv.Key] = kv.Value;
        return all;
    }

    static string HotkeySignature(Dictionary<string, int[]> keys)
    {
        return string.Join(";", keys.OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => kv.Key + "=" + string.Join(",", kv.Value)));
    }

    static void RegisterAll(Dictionary<string, int[]> snapshot = null)
    {
        // ถอดของเดิมก่อน (re-register ทุกครั้งที่ settings เปลี่ยน) — เช็คผลลัพธ์ด้วย
        foreach (var kv in Registered)
        {
            if (!UnregisterHotKey(IntPtr.Zero, kv.Key))
                Log("[hk] unregister id " + kv.Key + " FAILED err=" + Marshal.GetLastWin32Error());
        }
        Registered.Clear();

        var all = snapshot ?? LoadAllKeys();
        var signature = HotkeySignature(all);
        // Rebuild retry state from scratch; stale name-only retries can re-register
        // an obsolete shortcut after the user changes its modifiers/key.
        FailedKeys.Clear();
        UpgradableKeys.Clear();
        var usedChords = new HashSet<ulong>();
        foreach (var kv in all)
        {
            var keys = kv.Value;
            // vk = ตัวสุดท้ายที่ไม่ใช่ modifier; mods = รวมจากตัวที่เหลือ
            uint mods = 0; uint vk = 0;
            for (int i = 0; i < keys.Length; i++)
            {
                switch (keys[i])
                {
                    case 16: case 160: case 161: mods |= MOD_SHIFT; break;
                    case 17: case 162: case 163: mods |= MOD_CONTROL; break;
                    case 18: case 164: case 165: mods |= MOD_ALT; break;
                    case 91: case 92: mods |= MOD_WIN; break;
                    default: vk = (uint)keys[i]; break;
                }
            }
            // ข้ามคีย์ล้วน (ไม่มี modifier) — RegisterHotKey กลืนทั้งระบบจะหนักไป
            // (PTT แบบคีย์เดี่ยวทำงานเฉพาะในหน้า OSC ตามของแท้)
            if (vk == 0 || mods == 0)
            {
                Log("[hk] ข้าม " + kv.Key + " (ต้องมี modifier) keys=" + string.Join(",", keys));
                continue;
            }
            // Never mistake our own duplicate shortcut for an external NVIDIA conflict.
            // Do not synthesize a Shift fallback for a collision inside our settings.
            ulong chord = ((ulong)mods << 32) | vk;
            if (!usedChords.Add(chord))
            {
                Log("[hk] duplicate internal shortcut ignored: " + kv.Key);
                continue;
            }
            int id = _nextId++;
            uint hotkeyFlags = mods | MOD_NOREPEAT;
            if (RegisterHotKey(IntPtr.Zero, id, hotkeyFlags, vk))
            {
                Registered[id] = Tuple.Create(kv.Key, mods, vk);
                UpgradableKeys.Remove(kv.Key);   // ★ settings ใหม่ลงสำเร็จ = เลิก upgrade คีย์นี้ (กันทับคีย์ที่ user เลือก)
                FailedKeys.Remove(kv.Key);       // ★ [P2#5 Copilot] สำเร็จตรง = ล้าง failed เก่า
                Log("[hk] " + kv.Key + " = " + string.Join(",", keys) + " (id " + id + ")");
            }
            else
            {
                // ★ coexistence: NVIDIA App แท้จดชุด default (Alt+Z, Alt+F9 ฯลฯ) ไปก่อน
                //   → เลื่อนคีย์ด้วย Shift พิเศษ (Alt+Shift+Z, Alt+Shift+F9 …) ให้ได้ทุกตัว
                uint shifted = mods | MOD_SHIFT;
                uint fallbackFlags = shifted | MOD_NOREPEAT;
                if ((mods & MOD_SHIFT) == 0 && RegisterHotKey(IntPtr.Zero, id, fallbackFlags, vk))
                {
                    Registered[id] = Tuple.Create(kv.Key, shifted, vk);
                    UpgradableKeys[kv.Key] = Tuple.Create(mods, vk);   // เก็บ mods หลัก (Alt) — upgrade จะได้ Alt+X จริง
                    FailedKeys.Remove(kv.Key);
                    Log("[hk] " + kv.Key + " ชนกับแท้ → fallback = +Shift (id " + id + ")");
                }
                else {
                    FailedKeys[kv.Key] = Tuple.Create(mods, vk);
                    Log("[hk] " + kv.Key + " register FAILED err=" + Marshal.GetLastWin32Error() + " (จะ retry ทุก 10 วิ)");
                }
            }
        }
        _registeredHotkeySignature = signature;
        Log("[hk] รวมลงทะเบียน " + Registered.Count + " ตัว");
    }

    static void WatchSettings()
    {
        // Settings can be created after the helper starts. Keep default hotkeys
        // working instead of crashing when the NodeAPI directory is absent.
        var directory = Path.GetDirectoryName(SettingsPath);
        if (_settingsWatcher != null || !Directory.Exists(directory)) return;
        try
        {
        // Keep a strong reference for the lifetime of the message loop.
        _settingsWatcher = new FileSystemWatcher(directory, "*")
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size |
                NotifyFilters.FileName | NotifyFilters.CreationTime
        };
        FileSystemEventHandler changed = (sender, e) =>
        {
            if (IsSettingsFile(e.FullPath))
                Interlocked.Exchange(ref _settingsChangedAt, Environment.TickCount64);
        };
        _settingsWatcher.Changed += changed;
        _settingsWatcher.Created += changed;
        _settingsWatcher.Renamed += (sender, e) =>
        {
            if (IsSettingsFile(e.FullPath) || IsSettingsFile(e.OldFullPath))
                Interlocked.Exchange(ref _settingsChangedAt, Environment.TickCount64);
        };
        _settingsWatcher.Error += (sender, e) =>
        {
            Log("[settings] watcher error: " + e.GetException().Message);
            Interlocked.Exchange(ref _settingsChangedAt, Environment.TickCount64);
        };
        _settingsWatcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _settingsWatcher?.Dispose();
            _settingsWatcher = null;
            Log("[settings] file watcher unavailable; periodic probe remains active: " + ex.Message);
        }
    }

    static void ProbeSettings()
    {
        // Recover missed notifications (including directory/atomic replacements).
        // Only stat here; parse settings when a change was actually observed.
        if (Environment.TickCount64 - _lastSettingsProbe < 500) return;
        _lastSettingsProbe = Environment.TickCount64;
        if (_settingsWatcher == null) WatchSettings();
        try
        {
            var info = new FileInfo(SettingsPath);
            if (!info.Exists) return;
            var stamp = info.LastWriteTimeUtc.Ticks + ":" + info.Length;
            if (stamp == _settingsFileStamp) return;
            _settingsFileStamp = stamp;
            if (Interlocked.Read(ref _settingsChangedAt) < 0)
                Interlocked.Exchange(ref _settingsChangedAt, Environment.TickCount64);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    static void Main()
    {
        // dedup: หลาย instance จะแย่งกัน RegisterHotKey — เหลือตัวเดียว
        bool created;
        var single = new Mutex(true, @"Global\Duluka.nvsphelper", out created);
        if (!created)
        {
            Log("[instance] another nvsphelper is already running; exiting duplicate");
            single.Dispose();
            return;
        }
        GC.KeepAlive(single);

        LoadConfig();
        var initialMonitor = ReadMonitorEnabled();
        if (initialMonitor.HasValue)
            SetMonitorDesired(initialMonitor.Value);
        StartDispatcher();
        StartMonitorProbe();

        // Watch settings before registering, including atomic replacement.
        WatchSettings();
        if (Volatile.Read(ref _monitorDesired) == 0)
        {
            _monitorRegistered = false;
            Log("[monitor] started inside shortcuts editor; hotkeys not registered");
        }
        else RegisterAll();

        var m = new MSG();
        while (true)
        {
            // Win32 registrations are owned by this thread; release them for the
            // entire keyboard-shortcut editor lifetime so its key capture sees Alt+Z.
            SyncMonitorState();
            // Peek (ไม่บล็อก) — เพื่อเช็คธง re-register สม่ำเสมอ
            while (PeekMessageW(out m, IntPtr.Zero, 0, 0, 1) != 0)
            {
                if (m.message != WM_HOTKEY) continue;
                if (!_monitorRegistered || Volatile.Read(ref _monitorDesired) == 0) continue;
                int id = m.wParam.ToInt32();
                if (!Registered.TryGetValue(id, out var info)) continue;
                string name = info.Item1;
                string canonical = Canonical.TryGetValue(name, out var c) ? c : name;
                // MOD_NOREPEAT is the OS key-down/release owner. Deduplicate only
                // the same queued message; polling key-up can miss a rapid release.
                if (!MessageGate.TryAccept(canonical, info.Item3, m.time)) continue;
                if (canonical == "overlayfocus")
                {
                    // Snapshot foreground identity at keypress time, not after an HTTP delay.
                    var window = GetForegroundWindow();
                    GetWindowThreadProcessId(window, out var pid);
                    var title = new StringBuilder(512);
                    GetWindowText(window, title, title.Capacity);
                    string executable = "";
                    try { using var process = System.Diagnostics.Process.GetProcessById((int)pid); executable = process.ProcessName; } catch { }
                    EnqueuePost("http://127.0.0.1:59011/Duluka/v.1.0/GameFilter/Focus",
                        JsonSerializer.Serialize(new { pid, executable, title = title.ToString() }));
                }
                else
                {
                    Log("[hotkey] " + name + " (" + canonical + ") -> queued");
                    if (!string.IsNullOrEmpty(_fireUrl))
                        EnqueuePost(_fireUrl.Replace("{name}", Uri.EscapeDataString(canonical)));
                }
            }
            if (_monitorRegistered && (FailedKeys.Count > 0 || UpgradableKeys.Count > 0) && Environment.TickCount64 - _lastRetry > 10000) {
                _lastRetry = Environment.TickCount64;
                // Try upgrading to the primary key while preserving the active fallback.
                var ups = new Dictionary<string, Tuple<uint, uint>>(UpgradableKeys);
                foreach (var kv in ups) {
                    // Probe the primary binding while keeping the working
                    // fallback registered. A failed probe must not leave the
                    // user without a hotkey even for a short interval.
                    int nid = _nextId;
                    if (RegisterHotKey(IntPtr.Zero, nid, kv.Value.Item1 | MOD_NOREPEAT, kv.Value.Item2)) {
                        _nextId++;
                        var oldIds = Registered.Where(reg => reg.Value.Item1 == kv.Key)
                            .Select(reg => reg.Key).ToArray();
                        foreach (var oldId in oldIds) {
                            UnregisterHotKey(IntPtr.Zero, oldId);
                            Registered.Remove(oldId);
                        }
                        Registered[nid] = Tuple.Create(kv.Key, kv.Value.Item1, kv.Value.Item2);
                        UpgradableKeys.Remove(kv.Key);
                        FailedKeys.Remove(kv.Key);
                        Log("[hk] upgrade สำเร็จ: " + kv.Key + " ได้คีย์หลักกลับ (id " + nid + ")");
                    }
                    // Failed registration does not consume an ID or unregister
                    // the fallback; retry quietly on the next interval.
                }
                var retry = new Dictionary<string, Tuple<uint, uint>>(FailedKeys);
                foreach (var kv in retry) {
                    int rid = _nextId;
                    if (RegisterHotKey(IntPtr.Zero, rid, kv.Value.Item1 | MOD_NOREPEAT, kv.Value.Item2)) {
                        _nextId++;
                        Registered[rid] = Tuple.Create(kv.Key, kv.Value.Item1, kv.Value.Item2);
                        FailedKeys.Remove(kv.Key);
                        Log("[hk] retry สำเร็จ: " + kv.Key + " = " + kv.Value.Item1 + "+" + kv.Value.Item2 + " (id " + rid + ")");
                    }
                }
            }
            ProbeSettings();
            long settingsChangedAt = Interlocked.Read(ref _settingsChangedAt);
            if (settingsChangedAt >= 0 &&
                Environment.TickCount64 - settingsChangedAt >= SETTINGS_CHANGE_DEBOUNCE_MS &&
                Interlocked.CompareExchange(ref _settingsChangedAt, -1, settingsChangedAt) == settingsChangedAt)
            {
                var keys = LoadAllKeys(keepCurrentOnFailure: true);
                if (keys == null)
                {
                    Interlocked.Exchange(ref _settingsChangedAt, Environment.TickCount64);
                    Thread.Sleep(1);
                    continue;
                }
                var signature = HotkeySignature(keys);
                if (_monitorRegistered && Volatile.Read(ref _monitorDesired) != 0 &&
                    !string.Equals(signature, _registeredHotkeySignature, StringComparison.Ordinal))
                {
                    Log("[hk] hotkey settings changed — re-register immediately");
                    RegisterAll(keys);
                }
            }
            // Wait for OS messages without polling 1,000 times/sec or forcing 1ms system timers.
            // File changes and retry scheduling are checked at least every 15ms.
            if (MsgWaitForMultipleObjectsEx(0, IntPtr.Zero, 15, QS_ALLINPUT, MWMO_INPUTAVAILABLE) == WAIT_FAILED)
                Thread.Sleep(15);
        }
    }

    static void Post(HotkeyDispatchRequest item)
    {
        try
        {
            var req = (HttpWebRequest)WebRequest.Create(item.Url);
            req.Method = "POST"; req.ContentType = "application/json"; req.Proxy = null;
            req.Headers["X-OSC-Request-Id"] = item.RequestId;
            req.Headers["X-OSC-Created-At"] = item.CreatedAtUnixMs.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var bs = Encoding.UTF8.GetBytes(item.Body);
            req.ContentLength = bs.Length;
            req.Timeout = 3000;
            using (var s = req.GetRequestStream()) s.Write(bs, 0, bs.Length);
            using var r = (HttpWebResponse)req.GetResponse();
            Log("[hotkey] id=" + item.RequestId + " -> " + (int)r.StatusCode);
        }
        catch (Exception ex) { Log("[hotkey] id=" + item.RequestId + " fail: " + ex.Message); }
    }

}
