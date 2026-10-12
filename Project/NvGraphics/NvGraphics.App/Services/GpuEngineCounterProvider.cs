using System.Runtime.InteropServices;
using NvGraphics.Abstractions;
using NvGraphics.Models;

namespace NvGraphics.Services;

/// <summary>
/// GPU Engine utilization samples via the documented PDH API. Uses PdhAddEnglishCounterW
/// so behavior is independent of localized counter names. Never fabricates data: on any
/// failure TryCollect returns false with a reason.
/// </summary>
public sealed class GpuEngineCounterProvider : IEngineCounterProvider
{
    public const string CounterPath = @"\GPU Engine(*)\Utilization Percentage";

    /// <summary>Gap between the two PDH collection passes each sample (rate counters
    /// need two points; 200 ms keeps one TryCollect call comfortably under 1 s).</summary>
    public static readonly TimeSpan PdhCollectGap = TimeSpan.FromMilliseconds(200);

    public string SourceName => "PDH GPU Engine counters";
    /// <summary>Last measured collection duration, for overhead reporting.</summary>
    public TimeSpan LastCollectionDuration { get; private set; }

    public bool TryCollect(List<EngineSample> samples, out string error)
    {
        error = null;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        IntPtr query = IntPtr.Zero;
        IntPtr counter = IntPtr.Zero;
        IntPtr itemBuffer = IntPtr.Zero;
        try
        {
            int hr = Native.PdhOpenQueryW(IntPtr.Zero, IntPtr.Zero, out query);
            if (hr != 0) { error = PdhError("PdhOpenQueryW", hr); return false; }

            hr = Native.PdhAddEnglishCounterW(query, CounterPath, IntPtr.Zero, out counter);
            if (hr != 0) { error = PdhError("PdhAddEnglishCounterW (GPU Engine counter set missing?)", hr); return false; }

            // Utilization counters are rate-based: PDH needs TWO collection passes before
            // the formatted value is valid (one pass yields PDH_CSTATUS_INVALID_DATA).
            hr = Native.PdhCollectQueryData(query);
            if (hr != 0) { error = PdhError("PdhCollectQueryData", hr); return false; }
            System.Threading.Thread.Sleep(PdhCollectGap);
            hr = Native.PdhCollectQueryData(query);
            if (hr != 0) { error = PdhError("PdhCollectQueryData(2nd pass)", hr); return false; }

            int bufSize = 0, itemCount = 0;
            hr = Native.PdhGetFormattedCounterArrayW(counter, Native.PDH_FMT_DOUBLE,
                ref bufSize, ref itemCount, IntPtr.Zero);
            if (hr != Native.PDH_MORE_DATA && hr != 0)
            { error = PdhError("PdhGetFormattedCounterArrayW(size)", hr); return false; }

            itemBuffer = Marshal.AllocHGlobal(bufSize);
            hr = Native.PdhGetFormattedCounterArrayW(counter, Native.PDH_FMT_DOUBLE,
                ref bufSize, ref itemCount, itemBuffer);
            if (hr != 0) { error = PdhError("PdhGetFormattedCounterArrayW", hr); return false; }

            var stamp = DateTimeOffset.UtcNow;
            for (int i = 0; i < itemCount; i++)
            {
                IntPtr itemPtr = new IntPtr(itemBuffer.ToInt64() + i * Marshal.SizeOf<Native.PDH_FMT_COUNTERITEM_W>());
                var item = Marshal.PtrToStructure<Native.PDH_FMT_COUNTERITEM_W>(itemPtr);
                string instance = Marshal.PtrToStringUni(item.szName);
                if (ParseInstance(instance, item.fmtValue.doubleValue, stamp, out var sample))
                    samples.Add(sample);
                // Unparsable instance names are skipped, not guessed from.
            }
            return true;
        }
        catch (Exception ex)
        {
            error = $"PDH collection failed: {ex.Message}";
            return false;
        }
        finally
        {
            if (itemBuffer != IntPtr.Zero) Marshal.FreeHGlobal(itemBuffer);
            if (query != IntPtr.Zero) Native.PdhCloseQuery(query); // closes attached counters too
            sw.Stop();
            LastCollectionDuration = sw.Elapsed;
        }
    }

    private static readonly System.Text.RegularExpressions.Regex InstancePattern =
        new(@"^pid_(?<pid>\d+)_luid_(?<hi>0[xX][0-9a-fA-F]+)_(?<lo>0[xX][0-9a-fA-F]+)" +
            @"(?:_phys_\d+)?(?:_eng_\d+)?(?:_engtype_(?<eng>\w+))?$",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Parses "pid_19872_luid_0x00000000_0x00013667_phys_0_eng_0_engtype_3d"
    /// (phys/eng segments and the engine type may be absent on some instances).</summary>
    public static bool ParseInstance(string instance, double value, DateTimeOffset stamp, out EngineSample sample)
    {
        sample = null;
        if (string.IsNullOrEmpty(instance)) return false;
        var m = InstancePattern.Match(instance);
        if (!m.Success) return false;
        if (!int.TryParse(m.Groups["pid"].Value, out int pidValue)) return false;
        string luid = LuidUtility.NormalizeCounterLuid($"{m.Groups["hi"].Value}_{m.Groups["lo"].Value}");
        if (luid is null) return false;
        if (double.IsNaN(value) || double.IsInfinity(value) || value < 0) return false;
        sample = new EngineSample(pidValue, luid,
            m.Groups["eng"].Success ? m.Groups["eng"].Value.ToLowerInvariant() : "", value, stamp);
        return true;
    }

    private static string PdhError(string op, int hr) =>
        $"{op} failed: 0x{hr & 0xffffffff:x8}";

    private static class Native
    {
        public const int PDH_FMT_DOUBLE = 0x00000200;
        public const int PDH_MORE_DATA = unchecked((int)0x800007d2);

        [StructLayout(LayoutKind.Sequential)]
        public struct PDH_FMT_COUNTERVALUE_DOUBLE { public int CStatus; public double doubleValue; }

        [StructLayout(LayoutKind.Sequential)]
        public struct PDH_FMT_COUNTERITEM_W
        {
            public IntPtr szName;          // PWSTR instance name
            public PDH_FMT_COUNTERVALUE_DOUBLE fmtValue;
        }

        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        public static extern int PdhOpenQueryW(IntPtr reserved, IntPtr userData, out IntPtr queryHandle);

        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        public static extern int PdhAddEnglishCounterW(IntPtr queryHandle, string counterPath,
            IntPtr reserved, out IntPtr counterHandle);

        [DllImport("pdh.dll")]
        public static extern int PdhCollectQueryData(IntPtr queryHandle);

        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        public static extern int PdhGetFormattedCounterArrayW(IntPtr counterHandle, int dwFormat,
            ref int dwBufferSize, ref int dwItemCount, IntPtr ItemBuffer);

        [DllImport("pdh.dll")]
        public static extern int PdhCloseQuery(IntPtr queryHandle);
    }
}
