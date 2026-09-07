using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace RimSynapse.LocalTts
{
    /// <summary>One built-in Kokoro voice, described from its id and naming convention.</summary>
    public sealed class VoiceInfo
    {
        public string Id;            // e.g. "af_heart"
        public string GivenName;     // e.g. "Heart"
        public string LanguageName;  // e.g. "US English"
        public string EspeakLang;    // e.g. "en-us"
        public string Gender;        // "Female" / "Male"

        /// <summary>Dropdown label, e.g. "Heart — US English, Female".</summary>
        public string DisplayName => $"{GivenName} — {LanguageName}, {Gender}";
    }

    /// <summary>
    /// Builds the list of available voices by scanning the bundled voices folder. Voice ids follow
    /// Kokoro's convention: first letter = language, second letter = gender, then "_name".
    /// The catalog drives the settings dropdowns and tells the G2P which espeak language to use so
    /// each voice is pronounced correctly.
    /// </summary>
    public static class VoiceCatalog
    {
        // First-letter → (display language, espeak voice). English is what this mod bundles;
        // the rest are kept so an all-languages install still labels/pronounces correctly.
        private static readonly Dictionary<char, (string name, string espeak)> Langs =
            new Dictionary<char, (string, string)>
            {
                ['a'] = ("US English", "en-us"),
                ['b'] = ("British English", "en-gb"),
                ['e'] = ("Spanish", "es"),
                ['f'] = ("French", "fr-fr"),
                ['h'] = ("Hindi", "hi"),
                ['i'] = ("Italian", "it"),
                ['j'] = ("Japanese", "ja"),
                ['p'] = ("Portuguese", "pt-br"),
                ['z'] = ("Mandarin", "cmn"),
            };

        private static List<VoiceInfo> _all;
        private static Dictionary<string, VoiceInfo> _byId;

        public static IReadOnlyList<VoiceInfo> All
        {
            get { if (_all == null) Build(); return _all; }
        }

        public static VoiceInfo Get(string id)
        {
            if (_byId == null) Build();
            return id != null && _byId.TryGetValue(id, out var v) ? v : null;
        }

        /// <summary>espeak language for a voice id, defaulting to US English.</summary>
        public static string EspeakLangFor(string id) => Get(id)?.EspeakLang ?? "en-us";

        /// <summary>Re-scan the voices folder (e.g. after a new voice is downloaded).</summary>
        public static void Invalidate() { _all = null; _byId = null; }

        private static void Build()
        {
            _all = new List<VoiceInfo>();
            _byId = new Dictionary<string, VoiceInfo>();

            string dir = TtsAssets.VoicesDir;
            if (Directory.Exists(dir))
            {
                foreach (var path in Directory.GetFiles(dir, "*.bin"))
                {
                    string id = Path.GetFileNameWithoutExtension(path);
                    var info = Describe(id);
                    _all.Add(info);
                    _byId[id] = info;
                }
            }

            _all = _all
                .OrderBy(v => v.LanguageName)
                .ThenBy(v => v.Gender)
                .ThenBy(v => v.GivenName)
                .ToList();
        }

        private static VoiceInfo Describe(string id)
        {
            char langChar = id.Length > 0 ? id[0] : 'a';
            char genderChar = id.Length > 1 ? id[1] : 'f';

            var lang = Langs.TryGetValue(langChar, out var l) ? l : ("Unknown", "en-us");
            string gender = genderChar == 'm' ? "Male" : "Female";

            int us = id.IndexOf('_');
            string raw = us >= 0 && us < id.Length - 1 ? id.Substring(us + 1) : "Default";
            string given = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(raw);

            return new VoiceInfo
            {
                Id = id,
                GivenName = given,
                LanguageName = lang.Item1,
                EspeakLang = lang.Item2,
                Gender = gender,
            };
        }
    }
}
