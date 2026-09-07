using System.IO;

namespace LocalTts
{
    /// <summary>
    /// Resolves the on-disk locations of every bundled runtime asset relative to the mod's
    /// install directory. Populated once from the mod content pack's RootDir at load.
    ///
    /// Layout inside the mod folder:
    ///   Assemblies/                       managed dlls (ours + ONNX Runtime + System.* shims)
    ///   Resources/native/win-x64/         onnxruntime.dll, DirectML.dll, espeak-ng.dll, espeak-ng-data/
    ///   Resources/models/                 kokoro model, voices/, tokenizer vocab
    /// </summary>
    public static class TtsAssets
    {
        public static string RootDir { get; private set; }

        public static string NativeDir => Path.Combine(RootDir, "Resources", "native", "win-x64");
        public static string ModelsDir => Path.Combine(RootDir, "Resources", "models");
        public static string VoicesDir => Path.Combine(ModelsDir, "voices");

        public static string ModelPath => Path.Combine(ModelsDir, "kokoro-v1.0.onnx");
        public static string VocabPath => Path.Combine(ModelsDir, "kokoro-vocab.json");

        public static string EspeakDll => Path.Combine(NativeDir, "espeak-ng.dll");
        public static string EspeakDataDir => Path.Combine(NativeDir, "espeak-ng-data");

        public static void Init(string rootDir)
        {
            RootDir = rootDir;
        }

        /// <summary>True when the core files needed to synthesize are present on disk.</summary>
        public static bool ModelInstalled =>
            !string.IsNullOrEmpty(RootDir) && File.Exists(ModelPath) && Directory.Exists(VoicesDir);

        public static string VoiceFile(string voiceId) =>
            Path.Combine(VoicesDir, voiceId + ".bin");
    }
}
