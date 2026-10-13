using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;

namespace CaptureEngine.Audio
{
    public sealed class AudioWavSink : IAudioSink, IDisposable
    {
        private readonly string _path;
        private readonly int _channels;
        private readonly int _sampleRate;
        private readonly int _bitsPerSample;
        private readonly int _capacity;
        private readonly ConcurrentQueue<byte[]> _queue = new ConcurrentQueue<byte[]>();
        private readonly AutoResetEvent _wake = new AutoResetEvent(false);
        private readonly ManualResetEvent _stop = new ManualResetEvent(false);
        private Thread _writer;
        private FileStream _stream;
        private long _queuedBytes;
        private long _writtenBytes;
        private long _droppedBytes;
        private bool _started;
        private bool _completed;

        public long BytesWritten => Interlocked.Read(ref _writtenBytes);
        public long BytesDropped => Interlocked.Read(ref _droppedBytes);
        public long BytesEnqueued => BytesWritten + Interlocked.Read(ref _droppedBytes) + Interlocked.Read(ref _queuedBytes);

        public AudioWavSink(string path, int channels, int sampleRate, int bitsPerSample = 16, int maxBytes = 8 * 1024 * 1024)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("path required", nameof(path));
            if (channels < 1) throw new ArgumentOutOfRangeException(nameof(channels));
            if (sampleRate < 8000) throw new ArgumentOutOfRangeException(nameof(sampleRate));
            if (bitsPerSample != 16) throw new ArgumentOutOfRangeException(nameof(bitsPerSample));
            _path = Path.GetFullPath(path);
            _channels = channels;
            _sampleRate = sampleRate;
            _bitsPerSample = bitsPerSample;
            _capacity = Math.Max(64 * 1024, maxBytes);
        }

        public void Start()
        {
            if (_started) return;
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            _stream = new FileStream(_path, FileMode.Create, FileAccess.Write, FileShare.Read);
            WriteHeader(0);
            _writer = new Thread(WriterLoop) { IsBackground = true, Name = "AudioWavSink" };
            _started = true;
            _writer.Start();
        }

        public void Write(AudioPacket packet)
        {
            if (!_started || _completed || packet.Data == null || packet.Data.Length == 0) return;
            byte[] copy = new byte[packet.Data.Length];
            Buffer.BlockCopy(packet.Data, 0, copy, 0, copy.Length);
            while (Interlocked.Read(ref _queuedBytes) + copy.Length > _capacity)
            {
                if (!_queue.TryDequeue(out var dropped)) break;
                Interlocked.Add(ref _queuedBytes, -dropped.Length);
                Interlocked.Add(ref _droppedBytes, dropped.Length);
            }
            _queue.Enqueue(copy);
            Interlocked.Add(ref _queuedBytes, copy.Length);
            _wake.Set();
        }

        public AudioWavSink FinalizeSink(int timeoutMs = 5000)
        {
            Complete(timeoutMs);
            return this;
        }

        private void WriterLoop()
        {
            try
            {
                while (!_stop.WaitOne(50)) Drain();
                Drain();
            }
            catch
            {
            }
        }

        private void Drain()
        {
            while (_queue.TryDequeue(out var data))
            {
                Interlocked.Add(ref _queuedBytes, -data.Length);
                try
                {
                    _stream.Write(data, 0, data.Length);
                    Interlocked.Add(ref _writtenBytes, data.Length);
                }
                catch
                {
                    Interlocked.Add(ref _droppedBytes, data.Length);
                }
            }
        }

        public void Complete(int timeoutMs = 5000)
        {
            if (!_started || _completed) return;
            _completed = true;
            _stop.Set();
            _wake.Set();
            _writer?.Join(Math.Max(500, timeoutMs));
            try
            {
                WriteHeader(_writtenBytes);
                _stream.Flush();
            }
            catch
            {
            }
            _stream?.Dispose();
            _stream = null;
        }

        private void WriteHeader(long dataBytes)
        {
            _stream.Seek(0, SeekOrigin.Begin);
            using var w = new BinaryWriter(_stream, System.Text.Encoding.ASCII, true);
            int blockAlign = _channels * (_bitsPerSample / 8);
            int byteRate = _sampleRate * blockAlign;
            w.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
            w.Write((uint)Math.Min(uint.MaxValue, 36 + dataBytes));
            w.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt "));
            w.Write(16u);
            w.Write((ushort)1);
            w.Write((ushort)_channels);
            w.Write((uint)_sampleRate);
            w.Write((uint)byteRate);
            w.Write((ushort)blockAlign);
            w.Write((ushort)_bitsPerSample);
            w.Write(System.Text.Encoding.ASCII.GetBytes("data"));
            w.Write((uint)Math.Min(uint.MaxValue, dataBytes));
        }

        public void Dispose() => Complete();
    }
}
