using System;

namespace LocalTts
{
    /// <summary>
    /// Converts Kokoro's float32 waveform output (mono, 24 kHz, nominally in [-1, 1]) into the
    /// little-endian 16-bit PCM byte layout used when the broker stages a WAV file for a caller.
    /// (Direct in-mod playback via <see cref="TtsAudioPlayer"/> uses the float samples straight,
    /// with no PCM round-trip.)
    /// </summary>
    public static class PcmEncoder
    {
        public const int SampleRate = 24000;

        public static byte[] FloatToPcm16(float[] samples)
        {
            if (samples == null || samples.Length == 0) return Array.Empty<byte>();

            var pcm = new byte[samples.Length * 2];
            for (int i = 0; i < samples.Length; i++)
            {
                float v = samples[i];
                if (v > 1f) v = 1f;
                else if (v < -1f) v = -1f;

                short s = (short)Math.Round(v * short.MaxValue);
                pcm[i * 2] = (byte)(s & 0xFF);
                pcm[i * 2 + 1] = (byte)((s >> 8) & 0xFF);
            }
            return pcm;
        }
    }
}
