using System;
using System.IO;
using System.Runtime.InteropServices;
using RimSynapse;

namespace RimSynapse.LocalTts
{
    /// <summary>
    /// Preloads the bundled native DLLs (ONNX Runtime + DirectML + espeak-ng) by absolute path
    /// before any P/Invoke binds against them.
    ///
    /// RimWorld's Mono runtime resolves <c>[DllImport("onnxruntime")]</c> by bare module name, so
    /// the module has to already be in the process (or on the DLL search path). We add the mod's
    /// native folder to the search path and force-load each library up front. Once a module with a
    /// given base name is loaded, later imports bind to the resident handle.
    /// </summary>
    internal static class NativeLibraryLoader
    {
        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadLibrary(string lpFileName);

        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool SetDllDirectory(string lpPathName);

        private static bool _done;

        /// <summary>True once every required native library was resolved.</summary>
        public static bool Loaded { get; private set; }

        public static void EnsureLoaded()
        {
            if (_done) return;
            _done = true;

            try
            {
                string nativeDir = TtsAssets.NativeDir;
                if (!Directory.Exists(nativeDir))
                {
                    SynapseLogger.Warning($"[LocalTTS] Native folder missing: {nativeDir}. " +
                                          "Run download-assets.ps1 to install the model + native runtime.");
                    return;
                }

                // Put the native folder first on the search path so dependent DLLs (e.g. the
                // onnxruntime providers, DirectML) also resolve out of here.
                SetDllDirectory(nativeDir);

                bool ok = true;
                ok &= TryLoad(Path.Combine(nativeDir, "onnxruntime.dll"), required: true);
                // Provider-shared ships with some ORT builds; optional.
                TryLoad(Path.Combine(nativeDir, "onnxruntime_providers_shared.dll"), required: false);
                TryLoad(Path.Combine(nativeDir, "DirectML.dll"), required: false);
                ok &= TryLoad(Path.Combine(nativeDir, "espeak-ng.dll"), required: true);

                Loaded = ok;
                if (ok)
                    SynapseLogger.Message("[LocalTTS] Native libraries loaded (ONNX Runtime + espeak-ng).");
            }
            catch (Exception ex)
            {
                SynapseLogger.Error($"[LocalTTS] Native library preload failed: {ex.Message}");
            }
        }

        private static bool TryLoad(string path, bool required)
        {
            if (!File.Exists(path))
            {
                if (required)
                    SynapseLogger.Warning($"[LocalTTS] Required native library not found: {path}");
                return false;
            }

            IntPtr handle = LoadLibrary(path);
            if (handle == IntPtr.Zero)
            {
                int err = Marshal.GetLastWin32Error();
                SynapseLogger.Error($"[LocalTTS] LoadLibrary failed for {Path.GetFileName(path)} (Win32 {err}).");
                return false;
            }
            return true;
        }
    }
}
