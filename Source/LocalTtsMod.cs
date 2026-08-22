using UnityEngine;
using Verse;

namespace RimSynapse.LocalTts
{
    /// <summary>
    /// Mod entry point for RimSynapse Local Text-to-Speech. Wires up asset paths, registers with
    /// Core, and owns the async Kokoro engine.
    /// </summary>
    public class LocalTtsMod : Mod
    {
        public static LocalTtsMod Instance { get; private set; }

        public LocalTtsSettings Settings { get; private set; }
        public KokoroTtsEngine Engine { get; private set; }

        public LocalTtsMod(ModContentPack content) : base(content)
        {
            Instance = this;
            Settings = GetSettings<LocalTtsSettings>();

            // Resolve where the bundled model + native libraries live.
            TtsAssets.Init(content.RootDir);

            // Register with Core (no system prompt — this mod makes no LLM calls).
            SynapseCore.Register("rimsynapse.localtts", "RimSynapse Local TTS");

            // Spin up the async engine and warm it in the background so the first line is fast.
            Engine = new KokoroTtsEngine();
            Engine.Start();
            if (Settings.enabled && TtsAssets.ModelInstalled)
                Engine.Warmup();

            SynapseLogger.Message("[LocalTTS] RimSynapse Local Text-to-Speech loaded. " +
                                  (TtsAssets.ModelInstalled ? "Model present." : "Model NOT installed — run download-assets.ps1."));
        }

        public override string SettingsCategory() => "RimSynapse Local TTS";

        public override void DoSettingsWindowContents(Rect inRect)
        {
            var listing = new Listing_Standard();
            listing.Begin(inRect);

            listing.CheckboxLabeled("Enable local text-to-speech", ref Settings.enabled,
                "When off, all speech requests are ignored.");
            listing.GapLine();

            // ── Acceleration ──
            listing.Label("Hardware acceleration");
            listing.Gap(4f);
            if (listing.ButtonText($"Mode: {Settings.acceleration}  (click to cycle)"))
            {
                Settings.acceleration = (AccelerationMode)(((int)Settings.acceleration + 1) % 3);
            }
            var prev = GUI.color;
            GUI.color = new Color(0.6f, 0.6f, 0.6f);
            listing.Label("  Auto: GPU (DirectML) when an NVIDIA card is detected, else CPU");
            listing.Label("  GPU:  always try DirectML first (any DirectX 12 GPU)");
            listing.Label("  CPU:  force CPU inference");
            GUI.color = prev;

            listing.Gap(8f);
            listing.GapLine();

            // ── Voice + speed ──
            listing.Label($"Voice id: {Settings.defaultVoice}");
            Settings.defaultVoice = listing.TextEntry(Settings.defaultVoice).Trim();
            listing.Gap(4f);
            listing.Label($"Speed: {Settings.speed:F2}x");
            Settings.speed = Widgets.HorizontalSlider(listing.GetRect(22f), Settings.speed, 0.5f, 2.0f, roundTo: 0.05f);

            listing.Gap(8f);
            listing.GapLine();

            // ── Status ──
            listing.Label("Status");
            GUI.color = new Color(0.6f, 0.6f, 0.6f);
            listing.Label($"  Model installed: {TtsAssets.ModelInstalled}");
            listing.Label($"  NVIDIA GPU: {(GpuProbe.HasNvidia ? (GpuProbe.GpuName ?? "yes") : "not detected")}");
            if (Engine != null)
            {
                listing.Label($"  Engine ready: {Engine.Ready}   Backend: {Engine.ActiveProvider}   Queue: {Engine.QueueDepth}");
                if (!string.IsNullOrEmpty(Engine.LastError))
                    listing.Label($"  Last error: {Engine.LastError}");
            }
            GUI.color = prev;

            listing.Gap(8f);
            if (listing.ButtonText("Speak a test line"))
            {
                Engine?.Speak("Hello, colonist. Local text to speech is now online.");
            }

            listing.End();
        }
    }
}
