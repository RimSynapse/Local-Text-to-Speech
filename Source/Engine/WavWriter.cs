using System;
using System.IO;

namespace RimSynapse.LocalTts
{
    /// <summary>
    /// Writes Kokoro's mono float output as a standard 16-bit PCM WAV file — the format the broker
    /// stages for callers (any mod, or an external tool, can play or inspect a plain .wav). The
    /// file is written atomically: to a temp path, then moved into place, so a reader never sees a
    /// half-written file and "the file exists" is a safe readiness signal.
    /// </summary>
    public static class WavWriter
    {
        /// <summary>Encode <paramref name="samples"/> (mono, in [-1,1]) as a 16-bit PCM WAV byte array.</summary>
        public static byte[] Encode(float[] samples, int sampleRate)
        {
            byte[] pcm = PcmEncoder.FloatToPcm16(samples);
            const int channels = 1;
            const int bitsPerSample = 16;
            int byteRate = sampleRate * channels * (bitsPerSample / 8);
            int blockAlign = channels * (bitsPerSample / 8);
            int dataLen = pcm.Length;

            using (var ms = new MemoryStream(44 + dataLen))
            using (var w = new BinaryWriter(ms))
            {
                // RIFF header
                w.Write(new[] { 'R', 'I', 'F', 'F' });
                w.Write(36 + dataLen);                 // chunk size = 36 + Subchunk2Size
                w.Write(new[] { 'W', 'A', 'V', 'E' });
                // fmt subchunk
                w.Write(new[] { 'f', 'm', 't', ' ' });
                w.Write(16);                           // Subchunk1Size (PCM)
                w.Write((short)1);                     // AudioFormat = PCM
                w.Write((short)channels);
                w.Write(sampleRate);
                w.Write(byteRate);
                w.Write((short)blockAlign);
                w.Write((short)bitsPerSample);
                // data subchunk
                w.Write(new[] { 'd', 'a', 't', 'a' });
                w.Write(dataLen);
                w.Write(pcm);
                w.Flush();
                return ms.ToArray();
            }
        }

        /// <summary>
        /// Write a WAV atomically to <paramref name="destPath"/>. Returns true on success. The bytes
        /// land on a sibling temp file first and are then moved into place, so any reader that sees
        /// <paramref name="destPath"/> exist can trust it is complete and playable.
        /// </summary>
        public static bool WriteAtomic(float[] samples, int sampleRate, string destPath)
        {
            try
            {
                string dir = Path.GetDirectoryName(destPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                byte[] wav = Encode(samples, sampleRate);
                string tmp = destPath + ".tmp";
                File.WriteAllBytes(tmp, wav);
                if (File.Exists(destPath)) File.Delete(destPath);
                File.Move(tmp, destPath);
                return true;
            }
            catch (Exception ex)
            {
                TtsLog.Error($"[LocalTTS] Failed to stage WAV to {destPath}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Read a 16-bit PCM WAV back into mono float samples — used to replay a cached file. Parses
        /// the RIFF chunks to find <c>data</c> rather than assuming a fixed 44-byte header.
        /// </summary>
        public static bool TryRead(string path, out float[] samples, out int sampleRate)
        {
            samples = null;
            sampleRate = 0;
            try
            {
                byte[] b = File.ReadAllBytes(path);
                if (b.Length < 44) return false;

                sampleRate = BitConverter.ToInt32(b, 24);
                short bits = BitConverter.ToInt16(b, 34);
                if (bits != 16) return false;

                int p = 12; // past "RIFF"<size>"WAVE"
                int dataOff = -1, dataLen = 0;
                while (p + 8 <= b.Length)
                {
                    int sz = BitConverter.ToInt32(b, p + 4);
                    if (b[p] == 'd' && b[p + 1] == 'a' && b[p + 2] == 't' && b[p + 3] == 'a')
                    {
                        dataOff = p + 8;
                        dataLen = sz;
                        break;
                    }
                    p += 8 + sz + (sz & 1); // chunks are word-aligned
                }
                if (dataOff < 0) return false;

                int n = Math.Min(dataLen, b.Length - dataOff) / 2;
                samples = new float[n];
                for (int i = 0; i < n; i++)
                {
                    short s = (short)(b[dataOff + i * 2] | (b[dataOff + i * 2 + 1] << 8));
                    samples[i] = s / 32768f;
                }
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
