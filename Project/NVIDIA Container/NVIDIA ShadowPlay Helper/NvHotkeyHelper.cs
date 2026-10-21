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
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;

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
    [DllImport("winmm.dll")]
    static extern uint timeBeginPeriod(uint period);

    [StructLayout(LayoutKind.Sequential)]
    struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam; public IntPtr lParam; public uint time; public int ptX; public int ptY; }

    const uint WM_HOTKEY = 0x0312;
    const uint MOD_NOREPEAT = 0x4000;   // [P2#4 Copilot] 1 event ต่อ key-down (Win10+)
    const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_WIN = 0x8;
    static readonly string BuildRoot = FindBuildRoot();
    static readonly string ConfigPath = Path.Combine(BuildRoot, "NVIDIA Config", "nvsphelper.json");
    static readonly string SettingsPath = Path.Combine(BuildRoot, "Overlay On-Screen Control", "NVIDIA NodeAPI", "shadowplay-settings.json");
    static string _fireUrl = "http://127.0.0.1:59012/v1/hotkey/{name}";
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
        ["dvrtoggle"]            = new[] { 16, 18, 121 }, // Ctrl+Alt+F10
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
    static readonly Dictionary<string, uint> _awaitingKeyRelease = new Dictionary<string, uint>();
    static readonly Dictionary<string, long> _lastHotkeyMessage = new Dictionary<string, long>();
    const long HOTKEY_QUEUE_QUIET_MS = 150;

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
        var all = new Dictionary<string, int[]>(StringComparer.Ordinal);
        foreach (var kv in Defaults) all[kv.Key] = kv.Value;
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
        // ★ [P2#5 Copilot] reconcile: คีย์ที่ผู้ใช้ลบ/แก้ — ถอดออกจากตาราง retry ทั้งหมด
        var live = new System.Collections.Generic.HashSet<string>(all.Keys);
        var deadF = new System.Collections.Generic.List<string>();
        foreach (var fk in FailedKeys) if (!live.Contains(fk.Key)) deadF.Add(fk.Key);
        foreach (var n in deadF) { FailedKeys.Remove(n); Log("[hk] reconcile: ถอด " + n + " ออกจาก FailedKeys (ไม่มีใน settings แล้ว)"); }
        var deadU = new System.Collections.Generic.List<string>();
        foreach (var uk in UpgradableKeys) if (!live.Contains(uk.Key)) deadU.Add(uk.Key);
        foreach (var n in deadU) { UpgradableKeys.Remove(n); Log("[hk] reconcile: ถอด " + n + " ออกจาก UpgradableKeys"); }
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
        // Keep a strong reference for the lifetime of the message loop.
        _settingsWatcher = new FileSystemWatcher(Path.GetDirectoryName(SettingsPath), "*")
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

    static void ProbeSettings()
    {
        // Recover missed notifications (including directory/atomic replacements).
        // Only stat here; parse settings when a change was actually observed.
        if (Environment.TickCount64 - _lastSettingsProbe < 500) return;
        _lastSettingsProbe = Environment.TickCount64;
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
        if (timeBeginPeriod(1) != 0)
            Log("[hotkey] warning: could not set 1ms timer resolution");

        // Watch the settings file before registering so an update during startup
        // cannot be missed. Saves are atomic, so listen for replacement as well.
        WatchSettings();
        RegisterAll();

        var m = new MSG();
        while (true)
        {
            // Peek (ไม่บล็อก) — เพื่อเช็คธง re-register สม่ำเสมอ
            while (PeekMessageW(out m, IntPtr.Zero, 0, 0, 1) != 0)
            {
                if (m.message != WM_HOTKEY) continue;
                int id = m.wParam.ToInt32();
                if (!Registered.TryGetValue(id, out var info)) continue;
                string name = info.Item1;
                string canonical = Canonical.TryGetValue(name, out var c) ? c : name;
                _lastHotkeyMessage[canonical] = Environment.TickCount64;
                if (_awaitingKeyRelease.ContainsKey(canonical)) continue;
                _awaitingKeyRelease[canonical] = info.Item3;
                var n = canonical;
                var hotkeyName = name;
                ThreadPool.QueueUserWorkItem(delegate
                {
                    if (n == "overlayfocus")
                    {
                        var window = GetForegroundWindow();
                        GetWindowThreadProcessId(window, out var pid);
                        var title = new StringBuilder(512);
                        GetWindowText(window, title, title.Capacity);
                        string executable = "";
                        try { using var process = System.Diagnostics.Process.GetProcessById((int)pid); executable = process.ProcessName; } catch { }
                        Post("http://127.0.0.1:59011/Duluka/v.1.0/GameFilter/Focus", JsonSerializer.Serialize(new { pid, executable, title = title.ToString() }));
                        return;
                    }
                    Log("[hotkey] " + hotkeyName + " (" + n + ") -> fire");
                    if (_fireUrl.Length > 0) Post(_fireUrl.Replace("{name}", n));
                });
            }
            var released = new System.Collections.Generic.List<string>();
            foreach (var pending in _awaitingKeyRelease)
            {
                bool queueQuiet = !_lastHotkeyMessage.TryGetValue(pending.Key, out var lastMessage) ||
                                  Environment.TickCount64 - lastMessage >= HOTKEY_QUEUE_QUIET_MS;
                bool keyUp = (GetAsyncKeyState((int)pending.Value) & 0x8000) == 0;
                if (keyUp && queueQuiet) released.Add(pending.Key);
            }
            foreach (var key in released) _awaitingKeyRelease.Remove(key);
            if ((FailedKeys.Count > 0 || UpgradableKeys.Count > 0) && Environment.TickCount64 - _lastRetry > 10000) {
                _lastRetry = Environment.TickCount64;
                // ★ upgrade: คีย์ที่ใช้ fallback — ถอด fallback ลองคีย์หลัก ถ้าไม่ได้ใส่ fallback กลับ
                var ups = new Dictionary<string, Tuple<uint, uint>>(UpgradableKeys);
                foreach (var kv in ups) {
                    var toRemove = new System.Collections.Generic.List<int>();
                    foreach (var reg in Registered) { if (reg.Value.Item1 == kv.Key) toRemove.Add(reg.Key); }
                    foreach (var ridOld in toRemove) {
                        UnregisterHotKey(IntPtr.Zero, ridOld);
                        Registered.Remove(ridOld);
                    }
                    int nid = _nextId++;
                    if (RegisterHotKey(IntPtr.Zero, nid, kv.Value.Item1 | MOD_NOREPEAT, kv.Value.Item2)) {
                        Registered[nid] = Tuple.Create(kv.Key, kv.Value.Item1, kv.Value.Item2);
                        UpgradableKeys.Remove(kv.Key);
                        Log("[hk] upgrade สำเร็จ: " + kv.Key + " ได้คีย์หลักกลับ (id " + nid + ")");
                    } else {
                        // ★ [P2#5 Copilot] เช็คผล fallback ก่อนลง Registered
                        int rid2 = _nextId++;
                        uint fallbackMods = kv.Value.Item1 | MOD_SHIFT;
                        if (RegisterHotKey(IntPtr.Zero, rid2, fallbackMods | MOD_NOREPEAT, kv.Value.Item2)) {
                            Registered[rid2] = Tuple.Create(kv.Key, fallbackMods, kv.Value.Item2);
                            FailedKeys.Remove(kv.Key);
                            Log("[hk] upgrade ยังไม่ว่าง — ใช้ fallback ต่อ: " + kv.Key + " (id " + rid2 + ")");
                        } else {
                            FailedKeys[kv.Key] = kv.Value;
                            Log("[hk] upgrade fallback FAILED: " + kv.Key + " → ลง FailedKeys");
                        }
                    }
                }
                var retry = new Dictionary<string, Tuple<uint, uint>>(FailedKeys);
                foreach (var kv in retry) {
                    int rid = _nextId++;
                    if (RegisterHotKey(IntPtr.Zero, rid, kv.Value.Item1 | MOD_NOREPEAT, kv.Value.Item2)) {
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
                if (!string.Equals(signature, _registeredHotkeySignature, StringComparison.Ordinal))
                {
                    Log("[hk] hotkey settings changed — re-register immediately");
                    RegisterAll(keys);
                }
            }
            Thread.Sleep(1);
        }
    }

    static void Post(string url, string body = "{}")
    {
        try
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = "POST"; req.ContentType = "application/json"; req.Proxy = null;
            var bs = Encoding.UTF8.GetBytes(body);
            req.ContentLength = bs.Length;
            req.Timeout = 3000;
            using (var s = req.GetRequestStream()) s.Write(bs, 0, bs.Length);
            using var r = (HttpWebResponse)req.GetResponse();
            Log("[hotkey] " + url + " -> " + (int)r.StatusCode);
        }
        catch (Exception ex) { Log("[hotkey] " + url + " fail: " + ex.Message); }
    }

}
