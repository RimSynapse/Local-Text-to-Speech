using System.Text;
using LudeonTK;
using RimSynapse;

namespace RimSynapse.LocalTts.UI
{
    /// <summary>
    /// RimSynapse debug menu actions for Local TTS. These bypass any trigger conditions and
    /// exercise the synthesis pipeline directly, satisfying the RimSynapse debug-command
    /// validation gate. Each is headlessly triggerable via the dev-tools run_debug_action bridge.
    /// </summary>
    public static class DebugActions_LocalTts
    {
        private const string TestLine = "Hello, colonist. Local text to speech is now online.";

        /// <summary>Full path: synthesize the test line and play it (needs an active game for audio).</summary>
        [DebugAction("RimSynapse", "LocalTTS: Speak test line", actionType = DebugActionType.Action)]
        private static void SpeakTestLine()
        {
            var engine = LocalTtsMod.Instance?.Engine;
            if (engine == null) { SynapseLogger.Warning("[LocalTTS] Engine not available."); return; }
            engine.Speak(TestLine);
            SynapseLogger.Message("[LocalTTS] Queued test line for synthesis + playback.");
        }

        /// <summary>
        /// Headless proof-of-function: runs native load → espeak → tokenize → ONNX inference and
        /// logs the result WITHOUT needing audio playback (so it validates from the main menu too).
        /// </summary>
        [DebugAction("RimSynapse", "LocalTTS: Synthesize + dump stats (Log)", actionType = DebugActionType.Action)]
        private static void SynthesizeAndDump()
        {
            var engine = LocalTtsMod.Instance?.Engine;
            if (engine == null) { SynapseLogger.Warning("[LocalTTS] Engine not available."); return; }

            var settings = LocalTtsMod.Instance.Settings;
            engine.Synthesize(TestLine, settings.defaultVoice, settings.speed, samples =>
            {
                float seconds = samples.Length / (float)PcmEncoder.SampleRate;
                float peak = 0f;
                foreach (var s in samples) { float a = s < 0 ? -s : s; if (a > peak) peak = a; }
                SynapseLogger.Message(
                    $"[LocalTTS][debug] Synthesis OK: {samples.Length} samples ({seconds:F2}s @ {PcmEncoder.SampleRate}Hz), " +
                    $"peak amplitude {peak:F3}, backend {engine.ActiveProvider}.");
            });
            SynapseLogger.Message("[LocalTTS] Queued headless synthesis; watch the log for the result.");
        }

        /// <summary>Dump the current engine + asset status to the log.</summary>
        [DebugAction("RimSynapse", "LocalTTS: Dump engine status (Log)", actionType = DebugActionType.Action)]
        private static void DumpStatus()
        {
            var mod = LocalTtsMod.Instance;
            var sb = new StringBuilder();
            sb.AppendLine("[LocalTTS] Engine status:");
            sb.AppendLine($"  Root dir:        {TtsAssets.RootDir}");
            sb.AppendLine($"  Model installed: {TtsAssets.ModelInstalled} ({TtsAssets.ModelPath})");
            sb.AppendLine($"  Vocab source:    {(KokoroVocab.LoadedFromFile ? "kokoro-vocab.json" : "embedded fallback")} ({KokoroVocab.Map.Count} entries)");
            sb.AppendLine($"  NVIDIA GPU:      {(GpuProbe.HasNvidia ? (GpuProbe.GpuName ?? "yes") : "not detected")}");
            if (mod?.Engine != null)
            {
                sb.AppendLine($"  Engine ready:    {mod.Engine.Ready}");
                sb.AppendLine($"  Active backend:  {mod.Engine.ActiveProvider}");
                sb.AppendLine($"  Est. VRAM:       ~{mod.Engine.EstimatedVramMb:F0} MB");
                sb.AppendLine($"  Queue depth:     {mod.Engine.QueueDepth}");
                sb.AppendLine($"  Last error:      {mod.Engine.LastError ?? "(none)"}");
            }
            if (mod?.Settings != null)
                sb.AppendLine($"  Settings:        voice='{mod.Settings.defaultVoice}', speed={mod.Settings.speed:F2}, accel={mod.Settings.acceleration}, enabled={mod.Settings.enabled}");
            SynapseLogger.Message(sb.ToString());
        }
    }
}
