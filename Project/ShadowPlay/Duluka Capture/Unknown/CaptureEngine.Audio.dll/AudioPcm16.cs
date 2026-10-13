using System;

namespace CaptureEngine.Audio
{
    internal static class AudioPcm16
    {
        public static byte[] Convert(byte[] source, int bytes, int channels, int bitsPerSample)
        {
            if (source == null || bytes <= 0) return Array.Empty<byte>();
            bytes = Math.Min(bytes, source.Length);
            if (bitsPerSample != 32)
            {
                var copy = new byte[bytes];
                Buffer.BlockCopy(source, 0, copy, 0, bytes);
                return copy;
            }

            int frames = bytes / Math.Max(1, channels * 4);
            var output = new byte[frames * Math.Max(1, channels) * 2];
            int src = 0;
            int dst = 0;
            for (int i = 0; i < frames * channels; i++)
            {
                if (src + 4 > bytes) break;
                float sample = BitConverter.ToSingle(source, src);
                if (sample > 1f) sample = 1f;
                if (sample < -1f) sample = -1f;
                short pcm = (short)Math.Round(sample * 32767f);
                output[dst++] = (byte)(pcm & 0xff);
                output[dst++] = (byte)((pcm >> 8) & 0xff);
                src += 4;
            }
            return output;
        }
    }
}
