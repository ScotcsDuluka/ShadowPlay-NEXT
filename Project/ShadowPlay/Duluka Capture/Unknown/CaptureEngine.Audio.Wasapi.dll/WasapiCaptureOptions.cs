// WasapiCaptureOptions.cs — P13.2: knobs for WasapiPositionCapture.
//
// Defaults are the EXACT values P13.1 proved on OWNER's machine
// (Initialize attempt #1, flags=LOOPBACK, duration=100ms, 5ms poll —
// 6000/6000 + 5999/5999 packets with zero loss across two 60s runs).

using System;

namespace CaptureEngine.Audio.Wasapi
{
    /// <summary>Options for WasapiPositionCapture. All defaults are the
    /// P13.1-proven configuration — change only with a reason.</summary>
    public sealed class WasapiCaptureOptions
    {
        /// <summary>Loopback capture of the default RENDER device
        /// (system audio). False = microphone (default CAPTURE device).
        /// P13.1 note: mic and loopback each own their own position stream;
        /// both normalize into the same QPC domain (doc §5 Risk #4).</summary>
        public bool Loopback = true;

        /// <summary>Optional endpoint ID. Empty = default endpoint.</summary>
        public string DeviceId = "";

        /// <summary>Copy each packet's PCM bytes into WasapiPacket.Data.
        /// False = stamp-only packets (zero alloc, P13.1-style position
        /// logging); AudioTap v3 needs bytes, so leave true for recording.</summary>
        public bool IncludePcm = true;

        /// <summary>Sleep when no packet is pending. 5ms — the value the
        /// P13.1 spike ran with (arrival jitter p95 ~7ms, stamp side clean).</summary>
        public int PollIntervalMs = 5;

        /// <summary>First Initialize attempt's buffer duration, 100ns.
        /// 1,000,000 = 100ms; the fallback ladder then tries the engine
        /// default twice (see WasapiPositionCapture — ladder is verbatim
        /// from the spike).</summary>
        public long BufferDuration100ns = 1_000_000;
    }
}
