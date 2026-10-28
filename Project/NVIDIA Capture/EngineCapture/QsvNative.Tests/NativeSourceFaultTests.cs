using System.Diagnostics;
using System.Text.Json;
using DulukaEngine.Diagnostics;
using DulukaEngine.Encoder;
using DulukaEngine.Recording;
using DulukaEngine.Video;

static partial class Program
{
    // Test-owned producer. No desktop duplication, audio device, or GPU.
    sealed class NativeSyntheticCapture(string mode) : IVideoCaptureRuntime, IVideoCaptureTerminalHealth, IVideoBackendDiagnostics
    {
        string fault = "";
        Task? producer;
        public CaptureTarget ActualTarget => ContractTarget();
        public int OutputWidth => 320;
        public int OutputHeight => 180;
        public int OutputRefreshRate => 30;
        public long AccessLostCount => 0;
        public long TexturesCreated => 0;
        public long TexturesDisposed => 0;
        public long FramesPushed => 1;
        public long EmittedFrames => 1;
        public long DroppedFrames => 0;
        public long ReplacedFrames => 0;
        public long NoFrameCount => 1;
        public long ErrorCount => TerminalFault.Length == 0 ? 0 : 1;
        public IVideoBackendDiagnostics Diagnostics => this;
        public string TerminalFault => Volatile.Read(ref fault);
        public void Initialize(IVideoBackendContext context) => throw new InvalidOperationException("synthetic source must not initialize a device");
        public void Start(IVideoFrameSink sink)
        {
            producer = Task.Run(async () => {
                await Task.Delay(150);
                if (mode == "no-frame") { Volatile.Write(ref fault, "DeviceRemoved:before_first_frame"); return; }
                long now = (long)((decimal)Stopwatch.GetTimestamp() * 10000000m / Stopwatch.Frequency);
                var frame = new ContractFrame(0, 0, now);
                var outcome = sink.TryPush(FrameAcquisitionResult.Available(frame, 0, now));
                if (outcome == PushOutcome.Dropped) frame.Dispose();
                if (mode == "terminal") { await Task.Delay(250); Volatile.Write(ref fault, "DeviceRemoved:synthetic"); }
            });
        }
        public void Stop()
        {
            producer?.GetAwaiter().GetResult();
            if (mode == "stop-race") Volatile.Write(ref fault, "DeviceRemoved:during_stop");
            if (mode == "stop-timeout") Volatile.Write(ref fault, "source_stop_timeout_not_quiescent");
        }
        public void Dispose() { }
    }

    static void NativeSourceFaultSynthetic()
    {
        Directory.CreateDirectory(Evidence);
        string ffmpeg = Path.Combine(Repo(), "Project", "FFmpeg", "ffmpeg.exe");
        string ffprobe = Path.Combine(Path.GetDirectoryName(ffmpeg)!, "ffprobe.exe");
        string raw = Path.Combine(Evidence, "native-fault-synthetic.h264");
        var generate = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var arg in new[] { "-nostdin", "-y", "-v", "error", "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=30", "-frames:v", "60", "-c:v", "libx264", "-preset", "ultrafast", "-tune", "zerolatency", "-g", "1", "-bf", "0", "-f", "h264", raw }) generate.ArgumentList.Add(arg);
        using (var child = Process.Start(generate)!) {
            var stderr = child.StandardError.ReadToEndAsync(); var stdout = child.StandardOutput.ReadToEndAsync();
            if (!child.WaitForExit(30000)) { child.Kill(true); throw new TimeoutException("synthetic_h264_timeout"); }
            stdout.GetAwaiter().GetResult(); Check(child.ExitCode == 0, "synthetic H264 fixture generated: " + stderr.GetAwaiter().GetResult());
        }
        var probe = new ProcessStartInfo(ffprobe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var arg in new[] { "-v", "error", "-f", "h264", "-show_packets", "-show_entries", "packet=pos,size", "-of", "json", raw }) probe.ArgumentList.Add(arg);
        var bytes = File.ReadAllBytes(raw); var packets = new List<byte[]>();
        using (var child = Process.Start(probe)!) {
            var stdout = child.StandardOutput.ReadToEndAsync(); var stderr = child.StandardError.ReadToEndAsync();
            if (!child.WaitForExit(30000)) { child.Kill(true); throw new TimeoutException("synthetic_probe_timeout"); }
            using var json = JsonDocument.Parse(stdout.GetAwaiter().GetResult());
            Check(child.ExitCode == 0 && stderr.GetAwaiter().GetResult().Length == 0, "synthetic packet scan succeeds");
            foreach (var packet in json.RootElement.GetProperty("packets").EnumerateArray()) {
                int pos = int.Parse(packet.GetProperty("pos").GetString()!);
                int len = int.Parse(packet.GetProperty("size").GetString()!);
                packets.Add(bytes[pos..(pos + len)]);
            }
        }
        Check(packets.Count == 60, "fixture contains 60 test-owned H264 access units");
        foreach (string mode in new[] { "static", "terminal", "stop-race", "stop-timeout", "no-frame" }) {
            using var source = new NativeSyntheticCapture(mode);
            using var encoder = new ContractEncoder();
            int index = 0;
            int afterFault = 0;
            encoder.OnSubmit = _ => {
                if (source.TerminalFault.Length != 0) ++afterFault;
                int current = index++; if (current >= packets.Count) throw new InvalidOperationException("synthetic_packet_budget_exceeded");
                long pts = current * 10000000L / 30;
                return new EncoderSubmission(true, new EncodedPacket(new PacketMetadata(current, pts, pts, 333333, true, true, "H264", 0), packets[current], packets[current].Length));
            };
            var config = new SessionConfig { OutputPath = Path.Combine(Evidence, "native-source-" + mode + ".mp4"), FFmpegPath = ffmpeg,
                TargetFps = 30, DurationSeconds = 1, EncodeWidth = 320, EncodeHeight = 180, AudioEnabled = false, MicEnabled = false };
            using var session = new CaptureSession(source, encoder, config, new EngineLogger("native-source-fault", EngineLogger.LogLevel.Error, _ => {}));
            var result = session.Run();
            File.WriteAllText(Path.Combine(Evidence, "native-source-" + mode + ".json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
            if (mode != "no-frame") Check(result.FramesCaptured > 0, mode + ": synthetic source frame reached native session");
            if (mode == "static") {
                Check(result.Pass && result.FinalizationState == "Saved" && result.FramesDuplicated > 0,
                    "healthy static source stays valid without a false fault: " + result.ErrorMessage);
            } else {
                Check(!result.Pass && result.FinalizationState != "Saved" && result.ErrorMessage.Contains("session_video_source_fault:"),
                    mode + ": terminal source failure cannot report Saved: " + result.ErrorMessage);
                if (mode == "terminal") Check(result.ActualDurationSec < 0.9,
                    "terminal source fault ends session before the full duration");
                if (mode == "stop-race" || mode == "stop-timeout") Check(afterFault == 0,
                    mode + ": fault during Stop prevents final-frame and tail-fill submissions");
                if (mode == "no-frame") Check(result.FramesCaptured == 0 && result.FramesSubmitted == 0,
                    "fault before first frame cannot invent captured footage");
            }
        }
    }
}
