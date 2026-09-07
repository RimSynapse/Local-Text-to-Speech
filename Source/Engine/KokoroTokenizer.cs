using System.Collections.Generic;

namespace LocalTts
{
    /// <summary>
    /// Turns a string of IPA phonemes into Kokoro input token ids.
    ///
    /// Each phoneme character is mapped through <see cref="KokoroVocab"/>; characters not in the
    /// vocabulary are dropped (this mirrors the reference kokoro-onnx behavior). The returned list
    /// is the INNER token sequence — the model wrapper adds the leading/trailing 0 pad ids.
    /// Kokoro accepts at most 510 inner tokens per call.
    /// </summary>
    public static class KokoroTokenizer
    {
        public const int MaxTokens = 510;

        public static List<long> Encode(string phonemes)
        {
            var ids = new List<long>(phonemes?.Length ?? 0);
            if (string.IsNullOrEmpty(phonemes)) return ids;

            foreach (char c in phonemes)
            {
                if (KokoroVocab.TryGet(c, out int id))
                {
                    ids.Add(id);
                    if (ids.Count >= MaxTokens) break;
                }
            }
            return ids;
        }
    }
}
