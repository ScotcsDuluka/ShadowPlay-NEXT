using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using DulukaEngine.Audio;

// Deterministic, disk-bounded stress of the actual production WAV sink.
// No WASAPI, audio devices, capture, native NVENC, or application startup.
internal static class AudioStressTests
{
    private const int Rate = 48000;
    private const long T0 = 200_000_000L;
    private static int _passed;

    private static void Check(bool ok, string name)
    {
        if (!ok) throw new Exception("FAIL stress: " + name);
        _passed++;
    }

    private static AudioPacket P(AudioTrackKind kind, long at, int frames, int channels)
    {
        var buffer = new byte[frames * channels * 2];
        for (int i = 0; i < buffer.Length; i += 2)
        {
            buffer[i] = 0x23;
            buffer[i + 1] = 0x12;
        }
        return new AudioPacket(kind, at, frames, buffer, false, 0, 0, 0, Rate, channels);
    }

    private static void WavMatches(string path, int channels, int frames)
    {
        byte[] bytes = File.ReadAllBytes(path);
        int expectedBytes = frames * channels * 2;
        Check(bytes.Length == 44 + expectedBytes, "WAV exact PCM byte count");
        Check(System.Text.Encoding.ASCII.GetString(bytes, 0, 4) == "RIFF",
              "RIFF magic");
        Check(BitConverter.ToUInt32(bytes, 4) == bytes.Length - 8,
              "RIFF finalized length");
        Check(BitConverter.ToUInt32(bytes, 40) == expectedBytes,
              "WAV data header matches actual bytes");
        Check(BitConverter.ToUInt16(bytes, 22) == channels, "WAV channels");
        Check(BitConverter.ToUInt32(bytes, 24) == Rate, "WAV sample rate");
    }

    public static void RunAll()
    {
        string root = Path.Combine(AppContext.BaseDirectory, "Stress_" +
                                   Guid.NewGuid().ToString("N"));
        if (Directory.Exists(root)) throw new Exception("Refuse to overwrite stress data");
        Directory.CreateDirectory(root);
        var watch = Stopwatch.StartNew();
        try
        {
            // 96 independent sessions. Each output must be precisely
            // 60 ms long, regardless of gaps, duplicate T0 and packet order.
            for (int i = 0; i < 96; i++)
            {
                int channels = i % 2 == 0 ? 1 : 2;
                var track = channels == 1 ? AudioTrackKind.Microphone : AudioTrackKind.System;
                string output = Path.Combine(root, "cycle" + i + ".wav");
                using (var sink = new AudioTimelineWavSink(output))
                {
                    var early = P(track, T0 + 100_000L, 480, channels); // +10 to +20 ms
                    var late = P(track, T0 + 300_000L, 480, channels);  // +30 to +40 ms
                    if (i % 3 == 0)
                    {
                        sink.Write(late);
                        sink.Write(early);
                        sink.SetVideoStart(T0);
                    }
                    else
                    {
                        sink.SetVideoStart(T0);
                        sink.Write(early);
                        sink.Write(late);
                    }
                    sink.SetVideoStart(T0); // duplicate anchor must not rewind
                    sink.Complete(T0 + 600_000L); // +60ms
                    long wrote = sink.BytesWritten;
                    Check(wrote == 2880L * channels * 2, "60ms accounted frames");
                    Check(sink.BytesDropped == 0, "no unexpected PCM drops");
                    sink.Complete(T0 + 900_000L); // repeated finalize cannot extend
                    sink.Write(P(track, T0 + 800_000L, 480, channels)); // ignored
                    Check(wrote == sink.BytesWritten, "post-close writes ignored");
                }
                WavMatches(output, channels, 2880);
            }

            // 24 simultaneous packet producers to exercise lock ownership.
            // Audio may arrive out of order, but the writer must finalize one
            // valid 60ms file without races, corruption or unbounded waits.
            for (int i = 0; i < 24; i++)
            {
                string output = Path.Combine(root, "parallel" + i + ".wav");
                using (var sink = new AudioTimelineWavSink(output))
                {
                    sink.SetVideoStart(T0);
                    Task a = Task.Run(() =>
                        sink.Write(P(AudioTrackKind.System, T0 + 100_000L, 480, 2)));
                    Task b = Task.Run(() =>
                        sink.Write(P(AudioTrackKind.System, T0 + 300_000L, 480, 2)));
                    if (!Task.WaitAll(new[] { a, b }, 5000))
                        throw new Exception("parallel audio writers did not finish within 5s");
                    sink.Complete(T0 + 600_000L);
                }
                WavMatches(output, 2, 2880);
            }

            // Failure isolation: a file path occupied by a directory cannot
            // be opened as a WAV. It must not poison a fresh independent sink.
            string occupied = Path.Combine(root, "unwritable-as-a-file");
            Directory.CreateDirectory(occupied);
            bool rejected = false;
            try
            {
                using var blocked = new AudioTimelineWavSink(occupied);
                blocked.SetVideoStart(T0);
                blocked.Write(P(AudioTrackKind.System, T0, 480, 1));
            }
            catch (IOException) { rejected = true; }
            catch (UnauthorizedAccessException) { rejected = true; }
            Check(rejected, "directory-as-output refuses WAV startup");
            string recovered = Path.Combine(root, "recovery.wav");
            using (var sink = new AudioTimelineWavSink(recovered))
            {
                sink.SetVideoStart(T0);
                sink.Write(P(AudioTrackKind.System, T0, 480, 1));
                sink.Complete(T0 + 100_000L);
            }
            WavMatches(recovered, 1, 480);

            // 30 seconds of virtual timeline without capturing live audio.
            // Checks the many-block silence loop and the final partial chunk.
            // At 48kHz stereo this generates ~5.8 MiB; no huge test files.
            string longPath = Path.Combine(root, "virtual-30s.wav");
            using (var sink = new AudioTimelineWavSink(longPath))
            {
                sink.SetVideoStart(T0);
                sink.Write(P(AudioTrackKind.System, T0, 4800, 2));
                sink.Complete(T0 + 300_000_000L);
                Check(sink.BytesDropped == 0, "30s silence fill had no drops");
            }
            WavMatches(longPath, 2, Rate * 30);

            // A producer racing a finalizer must never reopen, corrupt,
            // or lengthen an already-finalized WAV.
            for (int i = 0; i < 24; i++)
            {
                string output = Path.Combine(root, "stop-race" + i + ".wav");
                using (var sink = new AudioTimelineWavSink(output))
                {
                    sink.SetVideoStart(T0);
                    sink.Write(P(AudioTrackKind.System, T0, 480, 1));
                    Task write = Task.Run(() =>
                        sink.Write(P(AudioTrackKind.System, T0 + 300_000L, 480, 1)));
                    Task stop = Task.Run(() => sink.Complete(T0 + 600_000L));
                    if (!Task.WaitAll(new[] { write, stop }, 5000))
                        throw new Exception("producer/finalize race exceeded 5s");
                    sink.Complete(T0 + 900_000L);
                }
                WavMatches(output, 1, 2880);
            }
            Check(watch.Elapsed < TimeSpan.FromSeconds(40),
                  "offline stress bounded under 40 seconds");
            Console.WriteLine($"AUDIO_STRESS_PASS checks={_passed}, sessions=146, " +
                              $"elapsed={watch.Elapsed.TotalSeconds:0.00}s");
        }
        finally
        {
            // Only delete the uniquely-created test directory. No user
            // recording, test project source or shared working folder touched.
            Directory.Delete(root, recursive: true);
        }
    }
}
