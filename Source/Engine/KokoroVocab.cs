using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json;

namespace RimSynapse.LocalTts
{
    /// <summary>
    /// The Kokoro phoneme vocabulary: a map from a single IPA/punctuation character to its integer
    /// token id. Kokoro expects <c>input_ids = [0, ...phoneme ids..., 0]</c>. The vocabulary is
    /// sparse — 114 symbols occupy ids across the 0..177 range; unused ids are simply not present.
    ///
    /// The map is loaded from the bundled <c>kokoro-vocab.json</c> when present. If that file is
    /// missing we fall back to an embedded copy of the exact same authoritative map (from the
    /// hexgrad/Kokoro-82M config), so tokenization is correct either way.
    /// </summary>
    public static class KokoroVocab
    {
        // Base64 of the authoritative {char: id} map (hexgrad/Kokoro-82M config.json "vocab").
        private const string EmbeddedVocabB64 =
            "eyI7IjoxLCI6IjoyLCIsIjozLCIuIjo0LCIhIjo1LCI/Ijo2LCLigJQiOjksIuKApiI6MTAsIlwiIjoxMSwiKCI6MTIsIikiOjEzLCLigJwiOjE0LCLigJ0iOjE1LCIgIjoxNiwizIMiOjE3LCLKoyI6MTgsIsqlIjoxOSwiyqYiOjIwLCLKqCI6MjEsIuG1nSI6MjIsIuqtpyI6MjMsIkEiOjI0LCJJIjoyNSwiTyI6MzEsIlEiOjMzLCJTIjozNSwiVCI6MzYsIlciOjM5LCJZIjo0MSwi4bWKIjo0MiwiYSI6NDMsImIiOjQ0LCJjIjo0NSwiZCI6NDYsImUiOjQ3LCJmIjo0OCwiaCI6NTAsImkiOjUxLCJqIjo1MiwiayI6NTMsImwiOjU0LCJtIjo1NSwibiI6NTYsIm8iOjU3LCJwIjo1OCwicSI6NTksInIiOjYwLCJzIjo2MSwidCI6NjIsInUiOjYzLCJ2Ijo2NCwidyI6NjUsIngiOjY2LCJ5Ijo2NywieiI6NjgsIsmRIjo2OSwiyZAiOjcwLCLJkiI6NzEsIsOmIjo3MiwizrIiOjc1LCLJlCI6NzYsIsmVIjo3Nywiw6ciOjc4LCLJliI6ODAsIsOwIjo4MSwiyqQiOjgyLCLJmSI6ODMsIsmaIjo4NSwiyZsiOjg2LCLJnCI6ODcsIsmfIjo5MCwiyaEiOjkyLCLJpSI6OTksIsmoIjoxMDEsIsmqIjoxMDIsIsqdIjoxMDMsIsmvIjoxMTAsIsmwIjoxMTEsIsWLIjoxMTIsIsmzIjoxMTMsIsmyIjoxMTQsIsm0IjoxMTUsIsO4IjoxMTYsIsm4IjoxMTgsIs64IjoxMTksIsWTIjoxMjAsIsm5IjoxMjMsIsm+IjoxMjUsIsm7IjoxMjYsIsqBIjoxMjgsIsm9IjoxMjksIsqCIjoxMzAsIsqDIjoxMzEsIsqIIjoxMzIsIsqnIjoxMzMsIsqKIjoxMzUsIsqLIjoxMzYsIsqMIjoxMzgsIsmjIjoxMzksIsmkIjoxNDAsIs+HIjoxNDIsIsqOIjoxNDMsIsqSIjoxNDcsIsqUIjoxNDgsIsuIIjoxNTYsIsuMIjoxNTcsIsuQIjoxNTgsIsqwIjoxNjIsIsqyIjoxNjQsIuKGkyI6MTY5LCLihpIiOjE3MSwi4oaXIjoxNzIsIuKGmCI6MTczLCLhtbsiOjE3N30=";

        private static Dictionary<char, int> _map;
        private static bool _fromFile;

        public static bool LoadedFromFile
        {
            get { if (_map == null) Load(); return _fromFile; }
        }

        public static IReadOnlyDictionary<char, int> Map
        {
            get
            {
                if (_map == null) Load();
                return _map;
            }
        }

        public static bool TryGet(char c, out int id) => Map.TryGetValue(c, out id);

        private static void Load()
        {
            string path = TtsAssets.VocabPath;
            if (File.Exists(path))
            {
                try
                {
                    _map = Parse(File.ReadAllText(path));
                    _fromFile = _map.Count > 0;
                }
                catch (Exception ex)
                {
                    TtsLog.Warning($"[LocalTTS] Failed to parse {Path.GetFileName(path)}: {ex.Message}. Using embedded vocab.");
                }
            }

            if (_map == null || _map.Count == 0)
            {
                _map = Parse(Encoding.UTF8.GetString(Convert.FromBase64String(EmbeddedVocabB64)));
                TtsLog.Message("[LocalTTS] Using embedded Kokoro vocabulary (kokoro-vocab.json not bundled).");
            }

            // Integrity canaries against known-good ids from the trained model.
            bool ok = _map.TryGetValue(' ', out int sp) && sp == 16
                      && _map.TryGetValue('A', out int a) && a == 24;
            if (!ok)
                TtsLog.Warning($"[LocalTTS] Vocabulary failed integrity check ({_map.Count} entries) — audio may be garbled.");
            else
                TtsLog.Message($"[LocalTTS] Vocabulary loaded: {_map.Count} entries ({(_fromFile ? "kokoro-vocab.json" : "embedded")}).");
        }

        private static Dictionary<char, int> Parse(string json)
        {
            var map = new Dictionary<char, int>();
            var raw = JsonConvert.DeserializeObject<Dictionary<string, int>>(json);
            if (raw != null)
            {
                foreach (var kv in raw)
                    if (!string.IsNullOrEmpty(kv.Key))
                        map[kv.Key[0]] = kv.Value;
            }
            return map;
        }
    }
}
