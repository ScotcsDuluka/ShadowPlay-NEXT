using System;
using System.Collections.Generic;

namespace CaptureEngine.Audio
{
    public enum AudioTrackKind
    {
        System = 0,
        Microphone = 1,
    }

    public sealed class AudioEngineConfig
    {
        public bool SystemEnabled { get; set; }
        public bool MicrophoneEnabled { get; set; }
        public string MicrophoneDeviceId { get; set; } = "";
        public string MicrophoneDeviceName { get; set; } = "";
        public int PollIntervalMs { get; set; } = 5;
        public long BufferDuration100ns { get; set; } = 1_000_000;
    }

    public readonly struct AudioPacket
    {
        public AudioTrackKind Track { get; }
        public long Pts100ns { get; }
        public int Frames { get; }
        public byte[] Data { get; }
        public bool IsSilence { get; }
        public long QpcPosition100ns { get; }
        public long DevicePositionFrames { get; }
        public int Flags { get; }
        public int SampleRate { get; }
        public int Channels { get; }

        public AudioPacket(AudioTrackKind track, long pts100ns, int frames,
                           byte[] data, bool isSilence,
                           long qpcPosition100ns, long devicePositionFrames, int flags,
                           int sampleRate = 0, int channels = 0)
        {
            Track = track;
            Pts100ns = pts100ns;
            Frames = frames;
            Data = data;
            IsSilence = isSilence;
            QpcPosition100ns = qpcPosition100ns;
            DevicePositionFrames = devicePositionFrames;
            Flags = flags;
            SampleRate = sampleRate;
            Channels = channels;
        }

        public int ByteCount => Data?.Length ?? 0;
    }

    public sealed class AudioTrackDiagnostics
    {
        public AudioTrackKind Track { get; internal set; }
        public string DeviceId { get; internal set; } = "";
        public int SampleRate { get; internal set; }
        public int Channels { get; internal set; }
        public long Packets { get; internal set; }
        public long Frames { get; internal set; }
        public long DataBytes { get; internal set; }
        public long SilenceBytes { get; internal set; }
        public long DroppedBytes { get; internal set; }
        public long FirstQpc100ns { get; internal set; }
        public long LastEnd100ns { get; internal set; }
        public long GapPackets { get; internal set; }
        public long IdleGapPackets { get; internal set; }
        public long QpcAnomalies { get; internal set; }
        public long CursorViolations { get; internal set; }
        public long TimestampErrorPackets { get; internal set; }
    }

    public sealed class AudioEngineDiagnostics
    {
        public List<AudioTrackDiagnostics> Tracks { get; } = new List<AudioTrackDiagnostics>();
        public long SessionStartQpc100ns { get; internal set; }
        public long VideoStartQpc100ns { get; internal set; }
        public long VideoAlignmentOffset100ns { get; internal set; }
        public bool VideoAligned { get; internal set; }

        public override string ToString()
        {
            var lines = new List<string>
            {
                $"[AudioEngine] sessionStartQpc100ns={SessionStartQpc100ns}",
                $"[AudioEngine] videoStartQpc100ns={VideoStartQpc100ns}",
                $"[AudioEngine] videoAligned={VideoAligned} offsetMs={VideoAlignmentOffset100ns / 10000.0:0.000}"
            };
            foreach (var t in Tracks)
            {
                lines.Add($"[AudioEngine] {t.Track} rate={t.SampleRate} ch={t.Channels} packets={t.Packets} frames={t.Frames} data={t.DataBytes}B silence={t.SilenceBytes}B dropped={t.DroppedBytes}B gaps={t.GapPackets} idle={t.IdleGapPackets} qpcJitter={t.QpcAnomalies} cursorViol={t.CursorViolations} tsErr={t.TimestampErrorPackets}");
            }
            return string.Join(Environment.NewLine, lines);
        }
    }

    public interface IAudioSink
    {
        void Write(AudioPacket packet);
    }
}
