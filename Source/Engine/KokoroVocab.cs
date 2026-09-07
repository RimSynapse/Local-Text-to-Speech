using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace LocalTts
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

        /// <summary>
        /// Parse the vocab's flat <c>{"char": id, ...}</c> JSON object into a char→id map. This is a
        /// purpose-built reader for exactly that shape (string keys, integer values) so the mod no
        /// longer depends on Core's bundled Newtonsoft.Json. Keys are single phoneme characters; only
        /// the first char of each key is used, matching the trained model's vocabulary.
        /// </summary>
        private static Dictionary<char, int> Parse(string json)
        {
            var map = new Dictionary<char, int>();
            if (string.IsNullOrEmpty(json)) return map;

            int i = 0;
            SkipWs(json, ref i);
            if (i >= json.Length || json[i] != '{') return map;
            i++; // consume '{'

            while (i < json.Length)
            {
                SkipWs(json, ref i);
                if (i < json.Length && json[i] == '}') break;
                if (i >= json.Length || json[i] != '"') break; // expected a key string

                string key = ParseString(json, ref i);
                SkipWs(json, ref i);
                if (i >= json.Length || json[i] != ':') break;
                i++; // consume ':'
                SkipWs(json, ref i);
                int value = ParseInt(json, ref i);

                if (!string.IsNullOrEmpty(key))
                    map[key[0]] = value;

                SkipWs(json, ref i);
                if (i < json.Length && json[i] == ',') { i++; continue; }
                break; // '}' or malformed — done
            }
            return map;
        }

        private static void SkipWs(string s, ref int i)
        {
            while (i < s.Length)
            {
                char c = s[i];
                if (c == ' ' || c == '\t' || c == '\n' || c == '\r') i++;
                else break;
            }
        }

        private static string ParseString(string s, ref int i)
        {
            var sb = new StringBuilder();
            i++; // consume opening '"'
            while (i < s.Length)
            {
                char c = s[i++];
                if (c == '"') break;
                if (c == '\\' && i < s.Length)
                {
                    char e = s[i++];
                    switch (e)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            if (i + 4 <= s.Length &&
                                int.TryParse(s.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int cp))
                            {
                                sb.Append((char)cp);
                                i += 4;
                            }
                            break;
                        default: sb.Append(e); break;
                    }
                }
                else sb.Append(c);
            }
            return sb.ToString();
        }

        private static int ParseInt(string s, ref int i)
        {
            int start = i;
            if (i < s.Length && (s[i] == '-' || s[i] == '+')) i++;
            while (i < s.Length && s[i] >= '0' && s[i] <= '9') i++;
            int.TryParse(s.Substring(start, i - start), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v);
            return v;
        }
    }
}
