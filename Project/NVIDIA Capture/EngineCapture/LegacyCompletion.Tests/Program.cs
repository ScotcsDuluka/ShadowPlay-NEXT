using System.Diagnostics;
using System.Reflection;
using NVIDIA_Capture;

// These children never acquire video/audio. They only receive the real legacy
// stop command and return controlled exit/error/timeout outcomes. The MP4 is
// generated from a lavfi color source before the tests and remains readable,
// deliberately reproducing the former "playable partial + Idle = Saved" bug.
internal static class Program
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private static int _checks;
    private static void Check(string name, bool value)
    {
        if (!value) throw new Exception("FAIL: " + name);
        _checks++;
        Console.WriteLine("PASS: " + name);
    }
    private static void Set(LegacyCaptureEngine engine, string field, object? value) =>
        typeof(LegacyCaptureEngine).GetField(field, PrivateInstance)!.SetValue(engine, value);
    private static object? Call(LegacyCaptureEngine engine, string method, params object[] arguments) =>
        typeof(LegacyCaptureEngine).GetMethod(method, PrivateInstance)!.Invoke(engine, arguments);

    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--video-child")
        {
            Console.WriteLine("READY");
            if (args[1] == "hang") { await Task.Delay(TimeSpan.FromSeconds(30)); return 0; }
            await Console.In.ReadLineAsync();
            if (args[1] == "fatal") Console.Error.WriteLine("[error] Synthetic encoder shutdown failure");
            return args[1] == "nonzero" ? 19 : 0;
        }
        if (args.Length != 1 || !File.Exists(args[0]))
            throw new ArgumentException("Supply the existing source ffmpeg.exe path. No desktop/audio inputs are used.");
        string ffmpeg = Path.GetFullPath(args[0]);
        string temporary = Path.Combine(Path.GetTempPath(), "ShadowPlay-LegacyCompletion-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            string fixture = Path.Combine(temporary, "synthetic.mp4");
            var generate = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (string arg in new[] { "-nostdin", "-v", "error", "-f", "lavfi", "-i", "color=c=blue:s=32x32:r=30", "-frames:v", "30", "-an", "-c:v", "libx264", "-pix_fmt", "yuv420p", fixture })
                generate.ArgumentList.Add(arg);
            using (var child = Process.Start(generate)!)
            {
                Task<string> stderr = child.StandardError.ReadToEndAsync();
                Task<string> stdout = child.StandardOutput.ReadToEndAsync();
                if (!child.WaitForExit(15000)) { child.Kill(true); child.WaitForExit(2000); throw new TimeoutException("Synthetic fixture generation timed out."); }
                Check("Synthetic media generated without capture devices", child.ExitCode == 0 && (await stderr).Length == 0 && File.Exists(fixture));
                await stdout;
            }
            await StopCase(ffmpeg, fixture, temporary, "zero", true);
            await StopCase(ffmpeg, fixture, temporary, "nonzero", false);
            await StopCase(ffmpeg, fixture, temporary, "fatal", false);
            await StopCase(ffmpeg, fixture, temporary, "hang", false);
            await StopCase(ffmpeg, fixture, temporary, "zero", false, requiresMux: true);
            Console.WriteLine($"PASS: {_checks} legacy completion assertions; no desktop/audio devices initialized.");
            return 0;
        }
        finally
        {
            // Delete only this test's newly created unique temporary directory.
            Directory.Delete(temporary, true);
        }
    }

    private static async Task StopCase(string ffmpeg, string fixture, string temporary, string mode,
        bool expectedSuccess, bool requiresMux = false)
    {
        string output = Path.Combine(temporary, mode + (requiresMux ? "-mux" : "") + ".mp4");
        File.Copy(fixture, output);
        using var engine = new LegacyCaptureEngine(new CaptureSettings { FFmpegPath = ffmpeg,
            OutputDirectory = temporary, SystemAudioCapture = false, MicCapture = false });
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false,
            CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        start.ArgumentList.Add("--video-child"); start.ArgumentList.Add(mode);
        using var child = Process.Start(start)!;
        try
        {
            Check(mode + ": test-owned child ready", await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)) == "READY");
            Set(engine, "_outputFile", output);
            Set(engine, "_ffmpegProcess", child);
            Set(engine, "_state", LegacyCaptureEngine.CaptureState.Recording);
            Call(engine, "BeginCompletion", output, requiresMux);
            Set(engine, "_completionStderrExpected", true);
            var errors = (DataReceivedEventHandler)Delegate.CreateDelegate(typeof(DataReceivedEventHandler), engine,
                typeof(LegacyCaptureEngine).GetMethod("OnStdErr", PrivateInstance)!);
            child.ErrorDataReceived += errors;
            child.BeginErrorReadLine();
            Check(mode + ": no completion published during session", !engine.LastCompletionSucceeded && engine.LastCompletedOutputPath == "");
            bool stopped = await engine.StopRecordingAsync().WaitAsync(TimeSpan.FromSeconds(20));
            Check(mode + ": legacy stop/state compatibility preserved", stopped && engine.State == LegacyCaptureEngine.CaptureState.Idle);
            var completed = engine.LastCompletion;
            Check(mode + ": completion bound to exact output", completed != null && completed.OutputPath == output && engine.LastCompletedOutputPath == output);
            Check(mode + ": success uses actual exit/failure/mux outcome", completed!.Succeeded == expectedSuccess && engine.LastCompletionSucceeded == expectedSuccess);
            Check(mode + ": readable partial remains on disk", File.Exists(output) && new FileInfo(output).Length > 0);
            Check(mode + ": failed lifecycle carries reason", expectedSuccess ? completed.FailureReason == "" : completed.FailureReason.Length > 0);
            engine.ForceStop();
            Check(mode + ": final completion immutable through cleanup", ReferenceEquals(completed, engine.LastCompletion));
            Call(engine, "BeginCompletion", output + ".next.mp4", false);
            Check(mode + ": restart cannot reuse previous success", engine.LastCompletion == null && !engine.LastCompletionSucceeded && engine.LastCompletedOutputPath == "");
            Check(mode + ": retained old snapshot stays immutable", completed.OutputPath == output && completed.Succeeded == expectedSuccess);
        }
        finally
        {
            try { if (!child.HasExited) { child.Kill(true); child.WaitForExit(2000); } } catch { }
            Set(engine, "_ffmpegProcess", null);
        }
    }
}
