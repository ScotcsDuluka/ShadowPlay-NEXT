using System;
using System.Collections.Generic;

namespace DulukaEngine.Audio
{
    /// <summary>
    /// Timeline-aware WAV output adapter. Capture/timeline ownership stays in
    /// AudioEngineSession; this class only aligns packets to a video QPC origin
    /// and serializes the resulting PCM through AudioWavSink.
    /// </summary>
    public sealed class AudioTimelineWavSink : IAudioSink, IDisposable
    {
        private readonly string _path;
        private readonly List<AudioPacket> _pending = new List<AudioPacket>();
        private readonly object _sync = new object();
        private AudioWavSink _writer;
        private int _sampleRate;
        private int _channels;
        private long _expectedPts100ns;
        private bool _aligned;
        private long _pendingBytes;
        private long _pendingDroppedBytes;
        private bool _completed;
        private const long MaxPendingBytes = 16L * 1024 * 1024;

        public AudioTimelineWavSink(string path) => _path = path ?? throw new ArgumentNullException(nameof(path));
        public long BytesWritten => _writer?.BytesWritten ?? 0;
        public long BytesDropped
        {
            get
            {
                lock (_sync)
                    return _pendingDroppedBytes + (_writer?.BytesDropped ?? 0);
            }
        }
        public string FilePath => _path;

        public void SetVideoStart(long videoStartQpc100ns)
        {
            if (videoStartQpc100ns <= 0) return;
            lock (_sync)
            {
                // The video's clock origin is immutable once established.
                // A duplicate T0 publish must not rewind the sample cursor:
                // doing so duplicates silence already committed to WAV.
                if (_aligned || _completed) return;
                _expectedPts100ns = videoStartQpc100ns;
                _aligned = true;
                FlushPendingLocked();
            }
        }

        public void Write(AudioPacket packet)
        {
            if (packet.Data == null || packet.Data.Length == 0) return;
            lock (_sync)
            {
                if (_completed) return;
                EnsureWriterLocked(packet);
                if (!_aligned)
                {
                    if (_pendingBytes + packet.Data.Length <= MaxPendingBytes)
                    {
                        _pending.Add(packet);
                        _pendingBytes += packet.Data.Length;
                    }
                    else
                    {
                        // A saturated pre-alignment buffer drops this
                        // packet. Keep the loss visible in accounting.
                        _pendingDroppedBytes += packet.Data.Length;
                    }
                    return;
                }
                WriteAlignedLocked(packet);
            }
        }

        private void EnsureWriterLocked(AudioPacket packet)
        {
            if (_writer != null) return;
            _sampleRate = packet.SampleRate;
            _channels = Math.Max(1, packet.Channels);
            if (_sampleRate > 0) _writer = new AudioWavSink(_path, _channels, _sampleRate, 16);
            _writer?.Start();
        }

        private void FlushPendingLocked()
        {
            if (!_aligned || _pending.Count == 0) return;
            _pending.Sort((a, b) => a.Pts100ns.CompareTo(b.Pts100ns));
            var copy = _pending.ToArray();
            _pending.Clear();
            _pendingBytes = 0;
            foreach (var packet in copy) WriteAlignedLocked(packet);
        }

        private void WriteAlignedLocked(AudioPacket packet)
        {
            if (_writer == null || _sampleRate <= 0 || _channels <= 0) return;
            int bytesPerFrame = _channels * 2;
            long duration = (long)packet.Frames * 10000000L / _sampleRate;
            long packetEnd = packet.Pts100ns + duration;
            if (packetEnd <= _expectedPts100ns) return;

            if (packet.Pts100ns > _expectedPts100ns)
                WriteSilenceLocked(packet.Pts100ns - _expectedPts100ns);

            int skipFrames = 0;
            if (packet.Pts100ns < _expectedPts100ns)
                skipFrames = Math.Min(packet.Frames,
                    (int)(((_expectedPts100ns - packet.Pts100ns) * _sampleRate + 9999999L) / 10000000L));

            // Packet.Frames can be larger than the supplied PCM buffer
            // (truncated device packet / conversion failure). Copy only
            // complete PCM frames and turn the missing tail into silence.
            // Never advance the timeline over samples that were not written.
            int remainingFrames = packet.Frames - skipFrames;
            int availableFrames = packet.Data.Length / bytesPerFrame;
            int copiedFrames = Math.Max(0, Math.Min(remainingFrames, availableFrames - skipFrames));
            if (copiedFrames > 0)
            {
                int offset = skipFrames * bytesPerFrame;
                int count = copiedFrames * bytesPerFrame;
                var data = new byte[count];
                Buffer.BlockCopy(packet.Data, offset, data, 0, count);
                _writer.Write(new AudioPacket(packet.Track, _expectedPts100ns,
                    copiedFrames, data, packet.IsSilence,
                    packet.QpcPosition100ns, packet.DevicePositionFrames, packet.Flags,
                    _sampleRate, _channels));
                _expectedPts100ns += (long)copiedFrames * 10000000L / _sampleRate;
            }
            if (_expectedPts100ns < packetEnd)
            {
                WriteSilenceLocked(packetEnd - _expectedPts100ns);
                // Preserve the sub-frame timestamp remainder as a clock
                // cursor only; PCM is always written in whole frames.
                if (_expectedPts100ns < packetEnd) _expectedPts100ns = packetEnd;
            }
        }

        private void WriteSilenceLocked(long duration100ns)
        {
            long frames = duration100ns * _sampleRate / 10000000L;
            if (frames <= 0) return;
            int bytesPerFrame = _channels * 2;
            byte[] zeros = new byte[Math.Min(65536, Math.Max(bytesPerFrame, 65536 - (65536 % bytesPerFrame)))];
            zeros = new byte[zeros.Length - (zeros.Length % bytesPerFrame)];
            long bytes = frames * bytesPerFrame;
            while (bytes > 0)
            {
                int n = (int)Math.Min(bytes, zeros.Length);
                // AudioWavSink.Write serializes the ENTIRE Data array, not
                // AudioPacket.Frames. Passing the full 64 KiB zero buffer for
                // a smaller tail chunk inflated the WAV and broke A/V sync.
                byte[] chunk = n == zeros.Length ? zeros : new byte[n];
                _writer.Write(new AudioPacket(AudioTrackKind.System, _expectedPts100ns,
                    n / bytesPerFrame, chunk, true, 0, 0, 0, _sampleRate, _channels));
                _expectedPts100ns += (long)(n / bytesPerFrame) * 10000000L / _sampleRate;
                bytes -= n;
            }
        }

        public void Complete(long sessionEndQpc100ns)
        {
            lock (_sync)
            {
                if (_completed) return;
                FlushPendingLocked();
                if (!_aligned && _pendingBytes > 0)
                {
                    // No video T0 was ever published, so queued packets
                    // cannot be aligned; account for all discarded data.
                    _pendingDroppedBytes += _pendingBytes;
                    _pendingBytes = 0;
                    _pending.Clear();
                }
                if (_writer != null && _aligned && sessionEndQpc100ns > _expectedPts100ns)
                    WriteSilenceLocked(sessionEndQpc100ns - _expectedPts100ns);
                _writer?.Complete(5000);
                _completed = true;
            }
        }

        public void Dispose()
        {
            lock (_sync)
            {
                _completed = true;
                _pending.Clear();
                _pendingBytes = 0;
                _writer?.Dispose();
                _writer = null;
            }
        }
    }
}
