using System;
using System.Collections.Generic;
using System.IO;

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

        /// <summary>
        /// Style vector for a blend of two voices: (1-amount)*primary + amount*blend. Falls back to
        /// the primary voice when the blend voice is empty/missing or the amount is ~0.
        /// </summary>
        public static float[] GetBlendedStyle(string primaryId, string blendId, float amount, int tokenCount)
        {
            float[] primary = GetStyle(primaryId, tokenCount);
            if (primary == null) return null;

            if (string.IsNullOrEmpty(blendId) || blendId == primaryId || amount <= 0.001f)
                return primary;

            float[] blend = GetStyle(blendId, tokenCount);
            if (blend == null) return primary;

            if (amount > 1f) amount = 1f;
            float keep = 1f - amount;
            var result = new float[Dim];
            for (int i = 0; i < Dim; i++)
                result[i] = primary[i] * keep + blend[i] * amount;
            return result;
        }

        private static float[] LoadVoice(string voiceId)
        {
            lock (_lock)
            {
                if (_cache.TryGetValue(voiceId, out var cached)) return cached;

                // Resolve bundled ids first; if the string isn't a known bundled voice, treat it as
                // a direct path to a caller-supplied .bin style vector (their own designed voice, a
                // Voicebox-style export, or an #11 RNG voice). This is what lets a broker caller pass
                // "af_heart" or "C:\path\myvoice.bin" through the same voice parameter.
                string path = TtsAssets.VoiceFile(voiceId);
                if (!File.Exists(path)) path = voiceId;
                if (!File.Exists(path))
                {
                    TtsLog.Warning($"[LocalTTS] Voice not found (neither a bundled id nor a file): {voiceId}");
                    _cache[voiceId] = null;
                    return null;
                }

                try
                {
                    byte[] bytes = File.ReadAllBytes(path);
                    int expected = Rows * Dim * sizeof(float);
                    if (bytes.Length < expected)
                    {
                        TtsLog.Error($"[LocalTTS] Voice '{voiceId}' is {bytes.Length} bytes, expected at least {expected}.");
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
                    TtsLog.Error($"[LocalTTS] Failed to load voice '{voiceId}': {ex.Message}");
                    _cache[voiceId] = null;
                    return null;
                }
            }
        }
    }
}
