using System;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

// DulukaPort v2 — Light-mode companion for the ported NVIDIA OSC.
// 1) holds the genuine single-instance EVENT (liveness for the launcher)
// 2) holds the pairing MAPPING with {"port","secret"} for Share's node-info
// 3) catches Alt+Z and drives the overlay via REST
class DulukaPort {
  [DllImport("kernel32.dll", SetLastError=true, CharSet=CharSet.Unicode)]
  static extern IntPtr CreateEventW(IntPtr attrs, bool manual, bool init, string name);
  [DllImport("kernel32.dll", SetLastError=true, CharSet=CharSet.Unicode)]
  static extern IntPtr CreateFileMappingW(IntPtr hFile, IntPtr attrs, uint prot, uint maxHi, uint maxLo, string name);
  [DllImport("kernel32.dll", SetLastError=true)]
  static extern IntPtr MapViewOfFile(IntPtr h, uint access, uint hi, uint lo, uint n);
  [DllImport("user32.dll", SetLastError=true)]
  static extern bool RegisterHotKey(IntPtr h, int id, uint mods, uint vk);
  [DllImport("user32.dll")]
  static extern int GetMessageW(out MSG m, IntPtr h, uint a, uint b);
  [StructLayout(LayoutKind.Sequential)]
  struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam; public IntPtr lParam; public uint time; public int ptX; public int ptY; }

  const uint WM_HOTKEY = 0x0312;
  const uint MOD_ALT = 0x1;
  const uint VK_Z = 0x5A;
  const uint PAGE_READWRITE = 0x04;
  const uint FILE_MAP_ALL = 0x0F;
  const string EVENT_NAME = @"Global\{1E6C4F0F-8ABF-4709-AAAD-0341CF2D44A7}";
  const string MAP_NAME = "{8BA1E16C-FC54-4595-9782-E370A5FBE8DA}";
  const string TOGGLE_URL = "http://127.0.0.1:59002/?hk=OpenShare";
  // Compatibility helper: load the existing per-user NvNode init dynamically.
  // Do not embed or print pairing credentials in source.
  static string ReadLegacyNodeJson() {
    string path = System.IO.Path.Combine(
      Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
      @"NVIDIA Corporation\NvNode\nvnode-init-duluka.json");
    if (!System.IO.File.Exists(path)) throw new InvalidOperationException("pairing_init_unavailable");
    string data = System.IO.File.ReadAllText(path);
    if (!data.Contains("\"port\"") || !data.Contains("\"secret\"") || data.Length > 4096)
      throw new InvalidOperationException("pairing_init_invalid");
    return data;
  }

  static void Main() {
    IntPtr ev = CreateEventW(IntPtr.Zero, false, false, EVENT_NAME);
    Console.WriteLine("[DulukaPort] event handle=0x" + ev.ToInt64().ToString("X") + " err=" + Marshal.GetLastWin32Error());

    IntPtr map = CreateFileMappingW(new IntPtr(-1), IntPtr.Zero, PAGE_READWRITE, 0, 4096, MAP_NAME);
    Console.WriteLine("[DulukaPort] map handle=0x" + map.ToInt64().ToString("X") + " err=" + Marshal.GetLastWin32Error());
    if (map != IntPtr.Zero) {
      IntPtr view = MapViewOfFile(map, FILE_MAP_ALL, 0, 0, 4096);
      if (view != IntPtr.Zero) {
        byte[] buf = new byte[4096];
        byte[] u16 = Encoding.ASCII.GetBytes(ReadLegacyNodeJson());
        byte[] a8 = Encoding.ASCII.GetBytes(ReadLegacyNodeJson());
        Array.Copy(u16, 0, buf, 0, u16.Length);                       // ANSI JSON, null-terminated, at offset 0 (decoded from genuine node)
        
        Marshal.Copy(buf, 0, view, 4096);
        Console.WriteLine("[DulukaPort] mapping written (u16@0, ansi@2048)");
      } else Console.WriteLine("[DulukaPort] MapViewOfFile FAILED err=" + Marshal.GetLastWin32Error());
    }

    var t = new Thread(new ThreadStart(MsgLoop));
    t.SetApartmentState(ApartmentState.STA);
    t.Start();
    Thread.Sleep(Timeout.Infinite);  // hold the event handle forever
  }

  static void MsgLoop() {
    // RegisterHotKey MUST live on the same thread that pumps GetMessage —
    // WM_HOTKEY is delivered to the registering thread's queue.
    if (!RegisterHotKey(IntPtr.Zero, 1, MOD_ALT, VK_Z))
      Console.WriteLine("[DulukaPort] RegisterHotKey Alt+Z FAILED err=" + Marshal.GetLastWin32Error());
    else
      Console.WriteLine("[DulukaPort] Alt+Z hotkey registered (hotkey thread)");
    MSG m = new MSG();
    while (GetMessageW(out m, IntPtr.Zero, 0, 0) > 0) {
      if (m.message == WM_HOTKEY && m.wParam.ToInt32() == 1) {
        Console.WriteLine("[DulukaPort] Alt+Z -> POST /?hk=OpenShare");
        ThreadPool.QueueUserWorkItem(delegate { Fire(); });
      }
    }
  }

  static void Fire() {
    try {
      var req = (HttpWebRequest)WebRequest.Create(TOGGLE_URL);
      req.Method = "POST";
      req.ContentType = "application/json";
      req.ContentLength = 2;
      var bs = Encoding.UTF8.GetBytes("{}");
      using (var s = req.GetRequestStream()) s.Write(bs, 0, bs.Length);
      using (var r = (HttpWebResponse)req.GetResponse())
        Console.WriteLine("[DulukaPort] toggle -> " + (int)r.StatusCode);
    } catch (Exception ex) {
      Console.WriteLine("[DulukaPort] toggle failed: " + ex.Message);
    }
  }
}
