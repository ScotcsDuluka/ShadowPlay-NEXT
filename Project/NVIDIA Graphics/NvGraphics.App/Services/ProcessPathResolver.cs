using System.Runtime.InteropServices;
using NvGraphics.Abstractions;

namespace NvGraphics.Services;

/// <summary>
/// PID → full executable path via OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION) +
/// QueryFullProcessImageNameW, with a cache invalidated when a PID's path changes
/// (catches PID reuse). Access-denied and exited processes return null — never a guess.
/// </summary>
public sealed class ProcessPathResolver : IProcessPathResolver
{
    private readonly Dictionary<int, (string Path, DateTime Stamp)> _cache = new();

    public string TryGetPath(int pid)
    {
        if (pid <= 0) return null;
        IntPtr h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        if (h == IntPtr.Zero)
        {
            // Exited or access denied: a previously cached path for this pid is stale.
            _cache.Remove(pid);
            return null;
        }
        try
        {
            var buf = new char[1024];
            int size = buf.Length;
            if (Native.QueryFullProcessImageNameW(h, 0, buf, ref size) != 0 && size > 0)
            {
                string path = new string(buf, 0, size);
                // PID reuse guard: if the cached path differs, the old entry is stale.
                if (_cache.TryGetValue(pid, out var cached) &&
                    !string.Equals(cached.Path, path, StringComparison.OrdinalIgnoreCase))
                {
                    _cache[pid] = (path, DateTime.UtcNow);
                }
                else
                {
                    _cache[pid] = (path, DateTime.UtcNow);
                }
                return path;
            }
            return null;
        }
        finally
        {
            Native.CloseHandle(h);
        }
    }

    private static class Native
    {
        public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern int QueryFullProcessImageNameW(IntPtr hProcess, int flags,
            char[] lpExeName, ref int lpSize);

        [DllImport("kernel32.dll")]
        public static extern bool CloseHandle(IntPtr h);
    }
}
