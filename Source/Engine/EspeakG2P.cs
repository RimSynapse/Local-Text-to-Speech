using System;
using System.Runtime.InteropServices;
using System.Text;

namespace LocalTts
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
        private static string _currentLang;

        /// <summary>Initialize the espeak-ng library once, pointing it at the bundled espeak-ng-data.</summary>
        public static bool EnsureInitialized()
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
                        TtsLog.Error("[LocalTTS] espeak_Initialize failed — check that espeak-ng-data is present.");
                        _failed = true;
                        return false;
                    }

                    _initialized = true;
                    TtsLog.Message($"[LocalTTS] espeak-ng initialized ({rate} Hz internal).");
                    return true;
                }
                catch (Exception ex)
                {
                    TtsLog.Error($"[LocalTTS] espeak-ng initialization threw: {ex.Message}");
                    _failed = true;
                    return false;
                }
            }
        }

        /// <summary>Select the espeak voice/language (cached; only re-set when it changes).</summary>
        private static void SetLanguage(string lang)
        {
            if (string.IsNullOrEmpty(lang) || lang == _currentLang) return;
            int err = EspeakNative.SetVoiceByName(lang);
            if (err != 0)
                TtsLog.Warning($"[LocalTTS] espeak SetVoiceByName('{lang}') returned {err}; using previous voice.");
            else
                _currentLang = lang;
        }

        /// <summary>
        /// Convert text into IPA phonemes using the given espeak language (e.g. "en-us", "en-gb").
        /// </summary>
        public static string Phonemize(string text, string lang = "en-us")
        {
            if (string.IsNullOrWhiteSpace(text)) return string.Empty;
            if (!EnsureInitialized()) return string.Empty;

            lock (_lock)
            {
                SetLanguage(lang);
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
                    TtsLog.Error($"[LocalTTS] Phonemize failed: {ex.Message}");
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
