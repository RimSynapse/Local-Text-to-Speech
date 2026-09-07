using System.Text;
using LudeonTK;

namespace LocalTts.UI
{
    /// <summary>
    /// Local TTS debug menu actions. These bypass any trigger conditions and
    /// exercise the synthesis pipeline directly, satisfying the debug-command
    /// validation gate. Each is headlessly triggerable via the dev-tools run_debug_action bridge.
    /// </summary>
    public static class DebugActions_LocalTts
    {
        private const string TestLine = "Hello, colonist. Local text to speech is now online.";

        /// <summary>Full path: synthesize the test line and play it (needs an active game for audio).</summary>
        [DebugAction("Local TTS", "LocalTTS: Speak test line", actionType = DebugActionType.Action)]
        private static void SpeakTestLine()
        {
            var engine = LocalTtsMod.Instance?.Engine;
            if (engine == null) { TtsLog.Warning("[LocalTTS] Engine not available."); return; }
            engine.Speak(TestLine);
            TtsLog.Message("[LocalTTS] Queued test line for synthesis + playback.");
        }

        /// <summary>
        /// Headless proof-of-function: runs native load → espeak → tokenize → ONNX inference and
        /// logs the result WITHOUT needing audio playback (so it validates from the main menu too).
        /// </summary>
        [DebugAction("Local TTS", "LocalTTS: Synthesize + dump stats (Log)", actionType = DebugActionType.Action)]
        private static void SynthesizeAndDump()
        {
            var engine = LocalTtsMod.Instance?.Engine;
            if (engine == null) { TtsLog.Warning("[LocalTTS] Engine not available."); return; }

            var settings = LocalTtsMod.Instance.Settings;
            engine.Synthesize(TestLine, settings.defaultVoice, settings.speed, samples =>
            {
                float seconds = samples.Length / (float)PcmEncoder.SampleRate;
                float peak = 0f;
                foreach (var s in samples) { float a = s < 0 ? -s : s; if (a > peak) peak = a; }
                TtsLog.Message(
                    $"[LocalTTS][debug] Synthesis OK: {samples.Length} samples ({seconds:F2}s @ {PcmEncoder.SampleRate}Hz), " +
                    $"peak amplitude {peak:F3}, backend {engine.ActiveProvider}.");
            });
            TtsLog.Message("[LocalTTS] Queued headless synthesis; watch the log for the result.");
        }

        /// <summary>
        /// Broker proof-of-function: fire a request through the public broker API, then poll the
        /// ticket to completion and log the staged file (path, size) — the headless validation of
        /// the async file-staging contract (#12). Also exercises the cache: a second identical
        /// request must resolve to the same file as an instant hit.
        /// </summary>
        [DebugAction("Local TTS", "LocalTTS: Broker request + stage file (Log)", actionType = DebugActionType.Action)]
        private static void BrokerStageFile()
        {
            string ticket = LocalTtsBroker.RequestSpeech(TestLine);
            if (ticket == null) { TtsLog.Warning("[LocalTTS][broker] RequestSpeech returned null (disabled/unavailable?)."); return; }
            TtsLog.Message($"[LocalTTS][broker] ticket={ticket} status={LocalTtsBroker.GetStatus(ticket)} — polling…");

            // Poll off the main thread so we don't block the game; log when it resolves.
            System.Threading.Tasks.Task.Run(() =>
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                int status;
                while ((status = LocalTtsBroker.GetStatus(ticket)) == LocalTtsBroker.STATUS_PENDING && sw.Elapsed.TotalSeconds < 60)
                    System.Threading.Thread.Sleep(100);

                if (status == LocalTtsBroker.STATUS_READY)
                {
                    string path = LocalTtsBroker.GetResultPath(ticket);
                    long size = 0; try { size = new System.IO.FileInfo(path).Length; } catch { }
                    string t2 = LocalTtsBroker.RequestSpeech(TestLine); // identical → cache hit
                    bool sameFile = t2 == ticket && LocalTtsBroker.GetStatus(t2) == LocalTtsBroker.STATUS_READY;
                    TtsLog.Message($"[LocalTTS][broker] READY in {sw.ElapsedMilliseconds}ms: {path} ({size} bytes). " +
                                   $"Cache hit on repeat: {sameFile} (ticket {t2}).");
                }
                else
                {
                    TtsLog.Warning($"[LocalTTS][broker] ended status={status} error={LocalTtsBroker.GetError(ticket)}");
                }
            });
        }

        /// <summary>Dump the current engine + asset status to the log.</summary>
        [DebugAction("Local TTS", "LocalTTS: Dump engine status (Log)", actionType = DebugActionType.Action)]
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
            TtsLog.Message(sb.ToString());
        }
    }
}
