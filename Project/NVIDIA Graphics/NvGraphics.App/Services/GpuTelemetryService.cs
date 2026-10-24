using System.Diagnostics;
using System.Globalization;
using NvGraphics.Models;

namespace NvGraphics.Services;

public sealed class GpuTelemetrySnapshot
{
    public DateTime TimestampUtc { get; init; } = DateTime.UtcNow;
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
/// Optional NVIDIA telemetry; never invents values or blocks on pipes indefinitely.
/// Even when invoked synchronously by Container, the external command has a deadline.
/// </summary>
public sealed class GpuTelemetryService
{
    public const string QueryArguments =
        "--query-gpu=name,pci.bus_id,utilization.gpu,memory.used,memory.total --format=csv,noheader,nounits";
    private readonly string _nvidiaSmiPath;
    private readonly string _args;
    private static readonly object SampleCacheGate = new();
    private static string CachedCommand;
    private static DateTimeOffset CachedAtUtc;
    private static GpuTelemetrySnapshot CachedSample;
    private static string CachedError = "";
    private static bool CachedSucceeded;
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(3);
    public bool LastAttemptSucceeded { get; private set; }
    public string LastError { get; private set; } = "";

    // Preserve ABI: existing NvContainer builds call .ctor(string).
    public GpuTelemetryService(string nvidiaSmiPath = null)
        : this(nvidiaSmiPath, null) { }

    public GpuTelemetryService(string nvidiaSmiPath, string arguments)
    {
        _nvidiaSmiPath = nvidiaSmiPath ?? LocateNvidiaSmi();
        _args = arguments ?? QueryArguments;
    }

    private static string LocateNvidiaSmi()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string[] candidates =
        [
            Path.Combine(programFiles, "NVIDIA Corporation", "NVSMI", "nvidia-smi.exe"),
            Path.Combine(Environment.SystemDirectory, "nvidia-smi.exe")
        ];
        foreach (var c in candidates)
            if (File.Exists(c)) return c;
        return "nvidia-smi.exe";
    }

    // Preserve ABI: existing NvContainer builds call Sample() with no parameters.
    public GpuTelemetrySnapshot Sample()
    {
        // NvContainer constructs this service per request. A short shared cache
        // coalesces repeated GPU Manager reads without caching stale GPU data long term.
        lock (SampleCacheGate)
        {
            var key = _nvidiaSmiPath + "\0" + _args;
            if (string.Equals(CachedCommand, key, StringComparison.Ordinal) &&
                DateTimeOffset.UtcNow - CachedAtUtc < CacheLifetime)
            {
                LastAttemptSucceeded = CachedSucceeded;
                LastError = CachedError;
                return CachedSample;
            }
            var result = Sample(TimeSpan.FromSeconds(4));
            CachedCommand = key;
            CachedAtUtc = DateTimeOffset.UtcNow;
            CachedSample = result;
            CachedSucceeded = LastAttemptSucceeded;
            CachedError = LastError;
            return result;
        }
    }

    public GpuTelemetrySnapshot Sample(TimeSpan? timeout) =>
        SampleAsync(timeout).GetAwaiter().GetResult();

    public async Task<GpuTelemetrySnapshot> SampleAsync(TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout ?? TimeSpan.FromSeconds(4));
        Process proc = null;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = _nvidiaSmiPath, Arguments = _args,
                RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true
            };
            proc = Process.Start(psi) ?? throw new IOException("nvidia-smi did not start");
            // Dispose only after timeout cleanup has a chance to terminate the child.
            {
                // Begin both pipe reads immediately, so neither pipe can deadlock the child.
                var stdout = proc.StandardOutput.ReadToEndAsync(deadline.Token);
                var stderr = proc.StandardError.ReadToEndAsync(deadline.Token);
                await proc.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
                var output = await stdout.ConfigureAwait(false);
                var err = await stderr.ConfigureAwait(false);
                if (proc.ExitCode != 0)
                {
                    LastAttemptSucceeded = false;
                    LastError = string.IsNullOrWhiteSpace(err)
                        ? "nvidia-smi exited with code " + proc.ExitCode : err.Trim();
                    return null;
                }
                var samples = new Dictionary<string, GpuSample>(StringComparer.OrdinalIgnoreCase);
                foreach (var raw in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    var columns = raw.Trim().TrimEnd('\r').Split(',');
                    if (columns.Length < 5) continue;
                    var pci = columns[1].Trim();
                    if (string.IsNullOrWhiteSpace(pci)) continue;
                    samples[pci] = new GpuSample
                    {
                        Name = columns[0].Trim(),
                        PciBusId = pci,
                        UtilizationPercent = TryParse(columns[2]),
                        MemoryUsedMiB = TryParse(columns[3]),
                        MemoryTotalMiB = TryParse(columns[4])
                    };
                }
                LastAttemptSucceeded = samples.Count > 0;
                LastError = LastAttemptSucceeded ? "" : "nvidia-smi returned no parseable rows.";
                return LastAttemptSucceeded
                    ? new GpuTelemetrySnapshot { Samples = samples, Source = "nvidia-smi" } : null;
            }
        }
        catch (OperationCanceledException)
        {
            LastAttemptSucceeded = false;
            LastError = "nvidia-smi timed out or telemetry was cancelled.";
            try { if (proc is { HasExited: false }) proc.Kill(entireProcessTree: true); }
            catch { /* Process may have exited; never allow cleanup to hide timeout. */ }
            return null;
        }
        catch (Exception ex)
        {
            LastAttemptSucceeded = false;
            LastError = ex.Message;
            try { if (proc is { HasExited: false }) proc.Kill(entireProcessTree: true); }
            catch { }
            return null;
        }
        finally { proc?.Dispose(); }
    }

    public static GpuSample ForAdapter(GpuTelemetrySnapshot snapshot, GpuAdapterInfo adapter)
        => snapshot?.Samples.TryGetValue(adapter?.PciBusId ?? "", out var sample) == true
            ? sample : null;

    private static double? TryParse(string value)
        => double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var result)
            && double.IsFinite(result) ? result : null;
}
