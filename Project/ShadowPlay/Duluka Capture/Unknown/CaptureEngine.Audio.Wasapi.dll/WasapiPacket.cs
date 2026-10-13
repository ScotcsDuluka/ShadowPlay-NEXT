// WasapiPacket.cs — P13.2: one WASAPI capture packet with its hardware stamps.
//
// This is the currency of the P13 clock design: every packet carries the
// device's OWN stamp of when its content is playing, so downstream code
// (AudioTap v3 gap math, SyncMath v2 anchors) subtracts stamps instead of
// guessing with Stopwatch arrival times.
//
// Stamp units — normalized here, at the source:
//   audioclient.h: GetBuffer's pu64QPCPosition is the performance counter
//   value AT THE TIME THE ENDPOINT READ THE DEVICE POSITION, already
//   expressed in 100-ns units (P13.1 field evidence: slope vs .NET
//   Stopwatch = 1.000003..1.000006 — the same counter, same scale).
//   It is therefore the wall time of the packet's content END, not its
//   first frame. The capture class assigns it UNCONVERTED:
//       QpcPosition100ns = qpcPos        (raw value == 100ns value)
//   Stopwatch.GetTimestamp() values are a DIFFERENT expression of the same
//   counter and need WasapiPositionCapture.StopwatchTicksTo100ns — never
//   apply that helper to a WASAPI qpcPosition (double conversion).

using System;

namespace CaptureEngine.Audio.Wasapi
{
    /// <summary>AUDCLNT_BUFFERFLAGS bits (audioclient.h — verified in P13.1).</summary>
    public static class WasapiPacketFlags
    {
        /// <summary>Packet content is all silence (stream start typically).
        /// P13.1 evidence: quiet-phase packets are genuine zero buffers
        /// WITHOUT this flag — never key silence math off it.</summary>
        public const int Discontinuity = 0x1;
        /// <summary>Timestamps derived from a known-error source.</summary>
        public const int Silent = 0x2;
        /// <summary>Packet not contiguous with the previous one.</summary>
        public const int TimestampError = 0x4;
    }

    /// <summary>One captured WASAPI packet: PCM bytes + hardware stamps.</summary>
    public struct WasapiPacket
    {
        /// <summary>Wall time of the packet's content END (the endpoint's
        /// read moment), in 100ns units exactly as WASAPI reported it.
        /// audioclient.h guarantees 100ns — do NOT convert again.</summary>
        public long QpcPosition100ns;

        /// <summary>The stamp exactly as WASAPI reported it — the same value
        /// as QpcPosition100ns (100ns, NOT Stopwatch ticks). Kept under the
        /// historical name for diagnostics only; engine math uses
        /// QpcPosition100ns.</summary>
        public long RawQpcTicks;

        /// <summary>Device sample-clock position of the packet's FIRST frame
        /// (frames rendered since stream start; cursor END = this + Frames).
        /// P13.1: ratio 1.000000 vs summed packet frames.</summary>
        public long DevicePositionFrames;

        /// <summary>Frames in this packet (P13.1 evidence: 480 = 10ms @ 48kHz
        /// on OWNER's machine, 100% of packets).</summary>
        public int Frames;

        /// <summary>AUDCLNT_BUFFERFLAGS bitmask (see WasapiPacketFlags).</summary>
        public int Flags;

        /// <summary>PCM copy of the packet in the device MIX FORMAT
        /// (float32 stereo on typical machines — see
        /// WasapiPositionCapture.MixFormatInfo). Null when
        /// WasapiCaptureOptions.IncludePcm == false.</summary>
        public byte[] Data;
    }
}

