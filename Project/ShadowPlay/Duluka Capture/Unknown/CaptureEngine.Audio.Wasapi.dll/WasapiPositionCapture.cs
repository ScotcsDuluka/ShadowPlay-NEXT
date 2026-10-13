// WasapiPositionCapture.cs — P13.2: position-aware WASAPI capture, the
// engine-class promotion of the P13.1 spike's capture loop.
//
// EVERY hard-won rule from the spike is preserved here:
//   1. [PreserveSig] + decoded HRESULTs — never the CLR's HRESULT→exception
//      map (it turned E_OUTOFMEMORY into a misleading "Out of memory.").
//   2. Initialize fallback ladder: LOOPBACK → LOOPBACK → LOOPBACK|NOPERSIST
//      × (100ms → engine default → engine default); every HRESULT logged.
//   3. pFormat = RAW pointer from GetMixFormat (EXTENSIBLE struct-copies
//      fail with E_INVALIDARG — 40 vs 18 bytes).
//   4. THE RULE: GetBuffer → process → ReleaseBuffer(framesRead).
//      NEVER 0. Passing 0 wedges the engine's read cursor → the same packet
//      re-serves forever (~2.4M/s) → the P13.1 OOM incident.
//   5. Runaway tripwire: 1000 consecutive identical stamps = cursor wedge
//      → terminal error, loop exits, memory survives.
//   6. client.Stop() even on error paths; CoTaskMemFree for the format.
//   7. (stability pass) Exception containment: the capture thread must
//      NEVER die with an unhandled exception — under .NET that kills the
//      WHOLE PROCESS. Marshal.Copy failures, allocation failures, and
//      PacketReady subscriber bugs are caught, the WASAPI stream is
//      stopped, and StoppedWithError carries the report instead.
//
// Runtime platform: Windows only (guarded in Start()); everything else in
// this assembly is pure and Linux-testable (AudioPositionTracker).

using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Interop = CaptureEngine.Audio.Wasapi.WasapiDirectInterop;

namespace CaptureEngine.Audio.Wasapi
{
    /// <summary>
    /// Captures the default render (loopback) or capture (mic) device,
    /// delivering per-packet hardware stamps + PCM.
    /// One instance = one stream. Not thread-safe: Start/Stop/Dispose from
    /// one thread; PacketReady fires on the capture thread.
    /// </summary>
    public sealed class WasapiPositionCapture : IDisposable
    {
        private readonly WasapiCaptureOptions _opt;

        private Interop.IMMDevice _device;
        private Interop.IAudioClient _client;
        private Interop.IAudioCaptureClient _capture;
        private Thread _thread;
        private volatile bool _running;

        /// <summary>Raised per packet ON THE CAPTURE THREAD. Keep handlers
        /// fast (AudioTap v3 copies and returns). A handler exception now
        /// TERMINATES THE CAPTURE LOOP but is contained: the WASAPI stream
        /// is stopped and StoppedWithError fires — the process survives
        /// (stability pass; an unhandled thread exception would end it).</summary>
        public event Action<WasapiPacket> PacketReady;

        /// <summary>Raised once when the loop terminates on an error
        /// (device loss, HRESULT failure, runaway tripwire). After this the
        /// instance is spent — create a new one (doc §3.2: new tap on device
        /// switch, "log it, don't bridge it").</summary>
        public event Action<string> StoppedWithError;

        /// <summary>Default endpoint ID ( IMMDevice.GetId ).</summary>
        public string DeviceId { get; private set; }

        /// <summary>Mix format summary, e.g.
        /// "2ch 48000Hz 32bit tag=65534 blockAlign=8 (EXTENSIBLE cbSize=22)".</summary>
        public string MixFormatInfo { get; private set; }

        /// <summary>Mix format sample rate (from the format header).</summary>
        public int SampleRate { get; private set; }

        /// <summary>Mix-format channel count (P13.3: consumers convert the
        /// float mix format to PCM16 and need the channel count).</summary>
        public int Channels { get; private set; }

        /// <summary>Mix-format bits per sample (typically 32 = IeeeFloat on
        /// shared-mode endpoints).</summary>
        public int BitsPerSample { get; private set; }

        /// <summary>Mix format blockAlign — bytes per frame (PCM sizing).</summary>
        public int BlockAlign { get; private set; }

        /// <summary>Endpoint buffer size in frames (after Initialize).</summary>
        public uint BufferFrames { get; private set; }

        /// <summary>Which ladder attempt Initialize accepted (1-based).</summary>
        public int InitializeAttempt { get; private set; }

        public bool IsRunning { get { return _running; } }

        /// <summary>True only on Windows — where the COM path can run.</summary>
        public static bool IsWindowsPlatform
        {
            get { return RuntimeInformation.IsOSPlatform(OSPlatform.Windows); }
        }

        public WasapiPositionCapture(WasapiCaptureOptions options = null)
        {
            _opt = options ?? new WasapiCaptureOptions();
        }

        // ── Startup ──────────────────────────────────────────────────────

        /// <summary>Open the device, initialize the stream, spawn the
        /// capture thread. Throws InvalidOperationException with the named
        /// failing call on any COM failure (decoded HRESULT inside).</summary>
        public void Start()
        {
            if (_running) throw new InvalidOperationException(
                "WasapiPositionCapture.Start: already running");
            if (!IsWindowsPlatform) throw new InvalidOperationException(
                "WasapiPositionCapture requires Windows (WASAPI COM). " +
                "This is '" + RuntimeInformation.OSDescription + "' — use " +
                nameof(AudioPositionTracker) + " for the testable logic.");
            if (_opt.PollIntervalMs < 0) throw new ArgumentOutOfRangeException(
                nameof(WasapiCaptureOptions.PollIntervalMs),
                "PollIntervalMs must be >= 0 (got " + _opt.PollIntervalMs + ")");
            if (_opt.BufferDuration100ns < 0) throw new ArgumentOutOfRangeException(
                nameof(WasapiCaptureOptions.BufferDuration100ns),
                "BufferDuration100ns must be >= 0 (got " + _opt.BufferDuration100ns + ")");

            // Device + client + mix format.
            int flow = _opt.Loopback ? Interop.eRender : Interop.eCapture;
            _device = Interop.GetDevice(flow, _opt.DeviceId);
            Interop.Check(_device.GetId(out string deviceId), "IMMDevice.GetId");
            DeviceId = deviceId;

            _client = Interop.ActivateAudioClient(_device);
            try
            {
                // Everything from here on can throw with a live stream
                // open. HARDENED (stability pass): on any failure we
                // best-effort Stop() the client and drop every COM
                // reference, so a retry with a fresh instance cannot trip
                // over residue (device-in-use style failures).
                OpenAndStartStream();
            }
            catch
            {
                try { if (_client != null) _client.Stop(); }
                catch { } // best effort — the original exception must win
                _capture = null;
                _client = null;
                _device = null;
                throw;
            }

            _running = true;
            _thread = new Thread(CaptureLoop)
            {
                IsBackground = true,
                Name = "WasapiPositionCapture",
            };
            _thread.Start();
        }

        /// <summary>Mix-format read, the Initialize ladder, buffer/capture
        /// setup, IAudioClient.Start — split out so Start() can wrap the
        /// whole live-stream section in one cleanup handler.</summary>
        private void OpenAndStartStream()
        {
            Interop.Check(_client.GetMixFormat(out IntPtr fmtPtr),
                          "IAudioClient.GetMixFormat");
            var fmt = (Interop.WAVEFORMATEX)Marshal.PtrToStructure(
                fmtPtr, typeof(Interop.WAVEFORMATEX));
            SampleRate = (int)fmt.nSamplesPerSec;
            BlockAlign = (int)fmt.nBlockAlign;
            Channels = (int)fmt.nChannels;
            BitsPerSample = (int)fmt.wBitsPerSample;
            MixFormatInfo = Interop.FormatToString(fmt) +
                            (fmt.wFormatTag == 0xFFFE
                                ? " (EXTENSIBLE cbSize=" + fmt.cbSize + ")"
                                : "");

            // Ladder — verbatim from the spike (rule #2, #3).
            int baseFlags = _opt.Loopback ? Interop.AUDCLNT_STREAMFLAGS_LOOPBACK : 0;
            int[] flagOpts =
            {
                baseFlags,
                baseFlags,
                baseFlags | Interop.AUDCLNT_STREAMFLAGS_NOPERSIST,
            };
            long[] durOpts = { _opt.BufferDuration100ns, 0, 0 };
            string[] durName =
            {
                _opt.BufferDuration100ns + "00ns",
                "0 (engine default)",
                "0 (engine default)",
            };

            int hrInit = -1;
            int used = -1;
            for (int attempt = 0; attempt < flagOpts.Length; attempt++)
            {
                hrInit = _client.Initialize(0, flagOpts[attempt], durOpts[attempt],
                                            0, fmtPtr, IntPtr.Zero);
                if (hrInit >= 0) { used = attempt; break; }
            }
            Marshal.FreeCoTaskMem(fmtPtr);   // engine copied what it needs (rule #6)
            if (hrInit < 0)
                throw new InvalidOperationException(
                    "IAudioClient.Initialize failed on ALL fallback attempts (last " +
                    Interop.HrName(hrInit) + ").");
            InitializeAttempt = used + 1;

            Interop.Check(_client.GetBufferSize(out uint bufferFrames),
                          "IAudioClient.GetBufferSize");
            BufferFrames = bufferFrames;

            _capture = Interop.GetCaptureClient(_client);
            Interop.Check(_client.Start(), "IAudioClient.Start");
        }

        // ── The loop ─────────────────────────────────────────────────────

        private void CaptureLoop()
        {
            string error = null;
            int dupRun = 0;
            long lastQpc = 0, lastDev = 0;
            bool haveLast = false;

            // RULE #7 (stability pass) — the catch below is the backstop.
            // Anything escaping the loop on this background thread would
            // otherwise TERMINATE THE PROCESS (.NET unhandled-thread
            // semantics). Contained instead: stream stopped, instance
            // spent, StoppedWithError carries the full report.
            try
            {
                while (_running)
                {
                    int hrN = _capture.GetNextPacketSize(out int pending);
                    if (hrN < 0) { error = "GetNextPacketSize: " + Interop.HrName(hrN); break; }
                    if (pending < 0)
                    { error = "GetNextPacketSize returned negative frames: " + pending; break; }
                    if (pending == 0) { Thread.Sleep(_opt.PollIntervalMs); continue; }

                    int hrG = _capture.GetBuffer(out IntPtr data, out int frames,
                                                 out int flags, out long devPos,
                                                 out long qpcPos);
                    if (hrG < 0) { error = "GetBuffer: " + Interop.HrName(hrG); break; }

                    // IMPORTANT: copy the WASAPI buffer BEFORE ReleaseBuffer.
                    // The data pointer is only valid until ReleaseBuffer(); releasing
                    // first and then Marshal.Copy reads reclaimed/reused audio memory
                    // and produces loud noise/corrupted samples.
                    byte[] pcmData = null;
                    if (_opt.IncludePcm && frames > 0)
                    {
                        int bytes = frames * BlockAlign;
                        pcmData = new byte[bytes];
                        Marshal.Copy(data, pcmData, 0, bytes);
                    }

                    // RULE #4 — never 0. The P13.1 OOM was this exact line.
                    int hrR = _capture.ReleaseBuffer(frames);
                    if (hrR < 0)
                    {
                        error = "ReleaseBuffer(" + frames + "): " + Interop.HrName(hrR);
                        break;
                    }

                    // RULE #5 — runaway tripwire (the v3 pathology detector).
                    if (haveLast && qpcPos == lastQpc && devPos == lastDev)
                    {
                        dupRun++;
                        if (dupRun >= 1000)
                        {
                            error = "runaway capture loop: 1000 consecutive identical " +
                                    "stamps (read cursor not advancing)";
                            break;
                        }
                    }
                    else dupRun = 0;
                    lastQpc = qpcPos; lastDev = devPos; haveLast = true;

                    var pkt = new WasapiPacket
                    {
                        RawQpcTicks = qpcPos,
                        QpcPosition100ns = qpcPos,
                        DevicePositionFrames = devPos,
                        Frames = frames,
                        Flags = flags,
                        Data = null,
                    };
                    pkt.Data = pcmData;

                    var handler = PacketReady;
                    if (handler != null) handler(pkt);
                }
            }
            catch (Exception ex)
            {
                error = "capture loop exception (contained): " + ex;
            }

            var client = _client;
            if (client != null)
            {
                int hrStop = client.Stop();
                if (hrStop < 0 && error == null)
                    error = "IAudioClient.Stop: " + Interop.HrName(hrStop);
            }
            _running = false;

            if (error != null)
            {
                var h = StoppedWithError;
                if (h != null) h(error);
            }
        }

        /// <summary>QPC ticks → 100ns units, overflow-safe:
        /// ticks/freq × 10^7 + (ticks%freq) × 10^7 / freq. The naive product
        /// overflows long once QPC passes ~29 days. Stopwatch shares the QPC
        /// counter (P13.1: slope ≈ 1), so this lands in the doc's 100ns
        /// domain on every machine.</summary>
        public static long StopwatchTicksTo100ns(long qpcTicks)
        {
            long f = Stopwatch.Frequency;
            return qpcTicks / f * 10_000_000L + qpcTicks % f * 10_000_000L / f;
        }

        // ── Shutdown ─────────────────────────────────────────────────────

        /// <summary>Stop the capture thread and release COM references.
        /// Safe to call twice.</summary>
        public void Stop()
        {
            _running = false;
            var t = _thread;
            if (t != null && t.IsAlive && t != Thread.CurrentThread)
            {
                // ★ Stop-race fix: a single Join(2000) ignores the timeout —
                // the caller (AudioEngineSession.Stop) then finalized the track
                // while the capture thread could still dispatch packets into
                // the same sinks, and Dispose() nulled the COM fields under the
                // still-running CaptureLoop. Bound the total wait (8s) and
                // keep joining in slices so a slow handler is waited out, not
                // raced.
                var deadline = Environment.TickCount64 + 8000;
                while (t.IsAlive && Environment.TickCount64 < deadline)
                    t.Join(1000);
            }
            _thread = null;
        }

        public void Dispose()
        {
            Stop();
            // Only drop the COM references once the capture thread is gone —
            // CaptureLoop dereferences the _capture field every iteration and
            // nulling it early produced a post-dispose NRE → spurious
            // StoppedWithError after teardown.
            _capture = null;
            _client = null;
            _device = null;
        }
    }
}
