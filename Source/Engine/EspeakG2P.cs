using System;
using System.Runtime.InteropServices;
using System.Text;
using RimSynapse;

namespace RimSynapse.LocalTts
{
    /// <summary>
    /// English grapheme-to-phoneme via espeak-ng, yielding IPA phonemes in the character set
    /// Kokoro was trained on. espeak-ng is not thread-safe, so every call is serialized behind a
    /// lock; in practice the engine only ever calls this from its single synthesis worker.
    /// </summary>
    public static class EspeakG2P
    {
        private static readonly object _lock = new object();
        private static bool _initialized;
        private static bool _failed;

        /// <summary>Initialize espeak-ng once, pointing it at the bundled espeak-ng-data folder.</summary>
        public static bool EnsureInitialized(string voice = "en-us")
        {
            if (_initialized) return true;
            if (_failed) return false;

            lock (_lock)
            {
                if (_initialized) return true;
                if (_failed) return false;

                try
                {
                    // path = the directory that CONTAINS espeak-ng-data.
                    int rate = EspeakNative.Initialize(
                        EspeakNative.AUDIO_OUTPUT_SYNCHRONOUS, 0, TtsAssets.NativeDir,
                        EspeakNative.INITIALIZE_DONT_EXIT);

                    if (rate < 0)
                    {
                        SynapseLogger.Error("[LocalTTS] espeak_Initialize failed — check that espeak-ng-data is present.");
                        _failed = true;
                        return false;
                    }

                    int err = EspeakNative.SetVoiceByName(voice);
                    if (err != 0)
                        SynapseLogger.Warning($"[LocalTTS] espeak SetVoiceByName('{voice}') returned {err}; continuing with default voice.");

                    _initialized = true;
                    SynapseLogger.Message($"[LocalTTS] espeak-ng initialized ({rate} Hz internal), voice '{voice}'.");
                    return true;
                }
                catch (Exception ex)
                {
                    SynapseLogger.Error($"[LocalTTS] espeak-ng initialization threw: {ex.Message}");
                    _failed = true;
                    return false;
                }
            }
        }

        /// <summary>Convert arbitrary English text into a string of IPA phonemes.</summary>
        public static string Phonemize(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return string.Empty;
            if (!EnsureInitialized()) return string.Empty;

            lock (_lock)
            {
                IntPtr buf = IntPtr.Zero;
                try
                {
                    buf = Utf8ToHGlobal(text);
                    IntPtr cur = buf;
                    var sb = new StringBuilder();

                    // espeak processes one clause per call and advances `cur`; loop to the end.
                    int guard = 0;
                    while (cur != IntPtr.Zero && Marshal.ReadByte(cur) != 0 && guard++ < 4096)
                    {
                        IntPtr result = EspeakNative.TextToPhonemes(
                            ref cur, EspeakNative.CHARS_UTF8, EspeakNative.PHONEMES_IPA);
                        string chunk = Utf8PtrToString(result);
                        if (!string.IsNullOrEmpty(chunk))
                        {
                            if (sb.Length > 0) sb.Append(' ');
                            sb.Append(chunk);
                        }
                    }

                    return sb.ToString();
                }
                catch (Exception ex)
                {
                    SynapseLogger.Error($"[LocalTTS] Phonemize failed: {ex.Message}");
                    return string.Empty;
                }
                finally
                {
                    if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf);
                }
            }
        }

        private static IntPtr Utf8ToHGlobal(string s)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(s);
            IntPtr p = Marshal.AllocHGlobal(bytes.Length + 1);
            Marshal.Copy(bytes, 0, p, bytes.Length);
            Marshal.WriteByte(p, bytes.Length, 0); // null terminator
            return p;
        }

        private static string Utf8PtrToString(IntPtr p)
        {
            if (p == IntPtr.Zero) return string.Empty;
            int len = 0;
            while (Marshal.ReadByte(p, len) != 0) len++;
            if (len == 0) return string.Empty;
            byte[] bytes = new byte[len];
            Marshal.Copy(p, bytes, 0, len);
            return Encoding.UTF8.GetString(bytes);
        }
    }
}
