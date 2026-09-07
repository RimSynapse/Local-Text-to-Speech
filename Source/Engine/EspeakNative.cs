using System;
using System.Runtime.InteropServices;

namespace RimSynapse.LocalTts
{
    /// <summary>
    /// Raw P/Invoke surface for espeak-ng (espeak-ng.dll). Only the entry points needed for
    /// grapheme-to-phoneme conversion are declared. The library is preloaded by
    /// <see cref="NativeLibraryLoader"/> so these bare-name imports bind to the bundled DLL.
    /// </summary>
    internal static class EspeakNative
    {
        // espeak_AUDIO_OUTPUT
        internal const int AUDIO_OUTPUT_SYNCHRONOUS = 0x02;

        // espeak_Initialize options
        internal const int INITIALIZE_DONT_EXIT = 0x8000;

        // espeak_TextToPhonemes textmode
        internal const int CHARS_UTF8 = 1;

        // espeak_TextToPhonemes phonememode: bit 1 set => IPA output (UTF-8)
        internal const int PHONEMES_IPA = 0x02;

        /// <summary>Returns the sample rate in Hz, or -1 on failure.</summary>
        [DllImport("espeak-ng", EntryPoint = "espeak_Initialize", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        internal static extern int Initialize(int output, int bufLength, string path, int options);

        /// <summary>Returns 0 (EE_OK) on success.</summary>
        [DllImport("espeak-ng", EntryPoint = "espeak_SetVoiceByName", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        internal static extern int SetVoiceByName(string name);

        /// <summary>
        /// Converts one clause of text to phonemes and advances <paramref name="textptr"/> past it.
        /// Returns a pointer to a null-terminated UTF-8 phoneme string (owned by espeak).
        /// </summary>
        [DllImport("espeak-ng", EntryPoint = "espeak_TextToPhonemes", CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr TextToPhonemes(ref IntPtr textptr, int textmode, int phonememode);

        [DllImport("espeak-ng", EntryPoint = "espeak_Terminate", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int Terminate();
    }
}
