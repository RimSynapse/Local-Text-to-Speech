using System;
using System.Collections.Generic;
using System.IO;
using RimSynapse;

namespace RimSynapse.LocalTts
{
    /// <summary>
    /// Loads Kokoro voice style vectors. Each voice ships as a raw float32 <c>.bin</c> of shape
    /// [510, 256] (a style row per possible token length). The style passed to the model is the
    /// row indexed by the number of inner tokens, clamped into range.
    /// </summary>
    public static class VoiceStyleBank
    {
        public const int Rows = 510;
        public const int Dim = 256;

        private static readonly Dictionary<string, float[]> _cache = new Dictionary<string, float[]>();
        private static readonly object _lock = new object();

        /// <summary>
        /// Returns the 256-float style vector for <paramref name="voiceId"/> at the given token
        /// count, or null if the voice file is missing/malformed.
        /// </summary>
        public static float[] GetStyle(string voiceId, int tokenCount)
        {
            float[] flat = LoadVoice(voiceId);
            if (flat == null) return null;

            int row = tokenCount;
            if (row < 0) row = 0;
            if (row > Rows - 1) row = Rows - 1;

            var style = new float[Dim];
            Array.Copy(flat, row * Dim, style, 0, Dim);
            return style;
        }

        private static float[] LoadVoice(string voiceId)
        {
            lock (_lock)
            {
                if (_cache.TryGetValue(voiceId, out var cached)) return cached;

                string path = TtsAssets.VoiceFile(voiceId);
                if (!File.Exists(path))
                {
                    SynapseLogger.Warning($"[LocalTTS] Voice file not found: {path}");
                    _cache[voiceId] = null;
                    return null;
                }

                try
                {
                    byte[] bytes = File.ReadAllBytes(path);
                    int expected = Rows * Dim * sizeof(float);
                    if (bytes.Length < expected)
                    {
                        SynapseLogger.Error($"[LocalTTS] Voice '{voiceId}' is {bytes.Length} bytes, expected at least {expected}.");
                        _cache[voiceId] = null;
                        return null;
                    }

                    var flat = new float[Rows * Dim];
                    Buffer.BlockCopy(bytes, 0, flat, 0, expected);
                    _cache[voiceId] = flat;
                    return flat;
                }
                catch (Exception ex)
                {
                    SynapseLogger.Error($"[LocalTTS] Failed to load voice '{voiceId}': {ex.Message}");
                    _cache[voiceId] = null;
                    return null;
                }
            }
        }
    }
}
