using System.Diagnostics;
using NvGraphics.Models;

namespace NvGraphics.Services;

public sealed class GpuTelemetrySnapshot
{
    public DateTime TimestampUtc { get; init; } = DateTime.UtcNow;
    /// <summary>Per-GPU sample keyed by PCI bus id ("00000000:01:00.0").</summary>
    public IReadOnlyDictionary<string, GpuSample> Samples { get; init; }
    public string Source { get; init; } = "";
}

public sealed class GpuSample
{
    public string PciBusId { get; init; }
    public string Name { get; init; }
    public double? UtilizationPercent { get; init; }
    public double? MemoryUsedMiB { get; init; }
    public double? MemoryTotalMiB { get; init; }
}

/// <summary>
/// Optional NVIDIA telemetry provider using nvidia-smi. Samples are keyed by PCI bus id so
/// they correlate with discovered adapters without relying on NVIDIA GPU index numbers.
/// If nvidia-smi is unavailable the service reports unavailable; it never fabricates data.
/// </summary>
public sealed class GpuTelemetryService
{
    private readonly string _nvidiaSmiPath;
    public bool LastAttemptSucceeded { get; private set; }
    public string LastError { get; private set; } = "";

    public GpuTelemetryService(string nvidiaSmiPath = null)
    {
        _nvidiaSmiPath = nvidiaSmiPath ?? LocateNvidiaSmi();
    }

    private static string LocateNvidiaSmi()
    {
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string[] candidates =
        [
            Path.Combine(programFiles, "NVIDIA Corporation", "NVSMI", "nvidia-smi.exe"),
            Path.Combine(Environment.SystemDirectory, "nvidia-smi.exe")
        ];
        foreach (var c in candidates)
            if (File.Exists(c)) return c;
        return "nvidia-smi.exe"; // fall back to PATH
    }

    public GpuTelemetrySnapshot Sample()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = _nvidiaSmiPath,
                Arguments = "--query-gpu=name,pci.bus_id,utilization.gpu,memory.used,memory.total --format=csv,noheader,nounits",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            string output = proc.StandardOutput.ReadToEnd();
            string err = proc.StandardError.ReadToEnd();
            if (!proc.WaitForExit(10000) || proc.ExitCode != 0)
            {
                LastAttemptSucceeded = false;
                LastError = string.IsNullOrWhiteSpace(err)
                    ? $"nvidia-smi exited with code {proc.ExitCode}." : err.Trim();
                return null;
            }

            var samples = new Dictionary<string, GpuSample>(StringComparer.OrdinalIgnoreCase);
            foreach (var rawLine in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var line = rawLine.Trim().TrimEnd('\r');
                var cols = line.Split(',');
                if (cols.Length < 5) continue;
                var busId = cols[1].Trim();
                samples[busId] = new GpuSample
                {
                    Name = cols[0].Trim(),
                    PciBusId = busId,
                    UtilizationPercent = TryParse(cols[2]),
                    MemoryUsedMiB = TryParse(cols[3]),
                    MemoryTotalMiB = TryParse(cols[4])
                };
            }
            LastAttemptSucceeded = samples.Count > 0;
            LastError = LastAttemptSucceeded ? "" : "nvidia-smi returned no parseable rows.";
            return LastAttemptSucceeded
                ? new GpuTelemetrySnapshot { Samples = samples, Source = "nvidia-smi" }
                : null;
        }
        catch (Exception ex)
        {
            LastAttemptSucceeded = false;
            LastError = ex.Message;
            return null;
        }
    }

    /// <summary>Returns the sample matching an adapter's PCI bus id, or null (never synthesized).</summary>
    public static GpuSample ForAdapter(GpuTelemetrySnapshot snapshot, GpuAdapterInfo adapter)
        => snapshot?.Samples.TryGetValue(adapter?.PciBusId ?? "", out var s) == true ? s : null;

    private static double? TryParse(string s) =>
        double.TryParse(s.Trim(), out var v) ? v : null;
}
