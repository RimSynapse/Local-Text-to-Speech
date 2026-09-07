using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace LocalTts
{
    /// <summary>
    /// Mod entry point for Local TTS. Stands up the main-thread pump and audio player, wires up
    /// asset paths, and owns the async Kokoro engine. Standalone — no RimSynapse Core dependency;
    /// Core integration (GPU stats) is optional and bound by reflection.
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

            // Stand up the main-thread pump before anything logs or synthesizes off-thread. This
            // runs in menu and in-game without a Game instance or a Harmony patch, and lets the
            // worker thread marshal logging/playback/broker callbacks back onto the main thread.
            MainThreadDispatcher.CaptureMainThread();
            var pumpGo = new GameObject("LocalTTS.MainThreadPump");
            Object.DontDestroyOnLoad(pumpGo);
            pumpGo.AddComponent<MainThreadPump>();
            TtsAudioPlayer.Init(pumpGo);

            // Capture a writable root for the broker's WAV cache while we're on the main thread
            // (Application.persistentDataPath is a per-user, writable location — the mod folder may
            // be read-only under Steam).
            LocalTtsBroker.CacheRoot = Application.persistentDataPath;

            // Resolve where the bundled model + native libraries live.
            TtsAssets.Init(content.RootDir);

            // Spin up the async engine and warm it in the background so the first line is fast.
            Engine = new KokoroTtsEngine();
            Engine.Start();
            if (Settings.enabled && TtsAssets.ModelInstalled)
                Engine.Warmup();

            TtsLog.Message("[LocalTTS] Local TTS loaded. " +
                                  (TtsAssets.ModelInstalled ? "Model present." : "Model NOT installed — run download-assets.ps1."));
        }

        public override string SettingsCategory() => "Local TTS";

        public override void DoSettingsWindowContents(Rect inRect)
        {
            var listing = new Listing_Standard();
            listing.Begin(inRect);
            var grey = new Color(0.6f, 0.6f, 0.6f);
            var prev = GUI.color;

            listing.CheckboxLabeled("Enable local text-to-speech", ref Settings.enabled,
                "When off, all speech requests are ignored.");
            listing.GapLine();

            // ── Voice ──
            listing.Label("Voice");
            if (listing.ButtonText(VoiceLabel(Settings.defaultVoice)))
                OpenVoiceMenu(id => Settings.defaultVoice = id, includeNone: false);

            // ── Blend (Kokoro's timbre control) ──
            listing.Gap(6f);
            string blendLabel = string.IsNullOrEmpty(Settings.blendVoice)
                ? "Blend with: None" : "Blend with: " + VoiceLabel(Settings.blendVoice);
            if (listing.ButtonText(blendLabel))
                OpenVoiceMenu(id => Settings.blendVoice = id, includeNone: true);

            GUI.color = grey;
            listing.Label("  Kokoro has no timbre dial — mixing a second voice is how you reshape it.");
            GUI.color = prev;

            if (!string.IsNullOrEmpty(Settings.blendVoice))
            {
                listing.Label($"  Blend amount: {Settings.blendAmount * 100f:F0}%  " +
                              $"({VoiceGiven(Settings.defaultVoice)} ↔ {VoiceGiven(Settings.blendVoice)})");
                Settings.blendAmount = Widgets.HorizontalSlider(
                    listing.GetRect(22f), Settings.blendAmount, 0f, 1f, roundTo: 0.05f);
            }

            listing.GapLine();

            // ── Speed + volume ──
            listing.Label($"Speed: {Settings.speed:F2}x");
            Settings.speed = Widgets.HorizontalSlider(listing.GetRect(22f), Settings.speed, 0.5f, 2.0f, roundTo: 0.05f);
            listing.Gap(4f);
            listing.Label($"Volume: {Settings.volume * 100f:F0}%");
            Settings.volume = Widgets.HorizontalSlider(listing.GetRect(22f), Settings.volume, 0.1f, 3.0f, roundTo: 0.05f);

            listing.GapLine();

            // ── Acceleration ──
            if (listing.ButtonText($"Hardware: {Settings.acceleration}  (Auto / GPU / CPU)"))
                Settings.acceleration = (AccelerationMode)(((int)Settings.acceleration + 1) % 3);

            listing.GapLine();

            // ── Tester ──
            listing.Label("Test any phrase — great for auditioning storyteller voices:");
            Settings.testPhrase = Widgets.TextArea(listing.GetRect(60f), Settings.testPhrase);
            listing.Gap(4f);

            Rect row = listing.GetRect(32f);
            if (Widgets.ButtonText(row.LeftHalf().ContractedBy(2f), "▶  Speak"))
            {
                if (!string.IsNullOrWhiteSpace(Settings.testPhrase))
                    Engine?.Speak(Settings.testPhrase);
            }
            if (Widgets.ButtonText(row.RightHalf().ContractedBy(2f), "■  Stop"))
                TtsAudioPlayer.Stop();

            // ── Status (compact) ──
            listing.Gap(6f);
            GUI.color = grey;
            string gpu = GpuProbe.HasNvidia ? (GpuProbe.GpuName ?? "NVIDIA") : "no NVIDIA";
            listing.Label($"  Backend: {Engine?.ActiveProvider ?? "—"} ({gpu})   " +
                          $"Voices: {VoiceCatalog.All.Count}   Model: {(TtsAssets.ModelInstalled ? "ok" : "MISSING")}");
            if (Engine != null && !string.IsNullOrEmpty(Engine.LastError))
            {
                GUI.color = Color.yellow;
                listing.Label($"  Last error: {Engine.LastError}");
            }
            GUI.color = prev;

            listing.End();
        }

        /// <summary>Dropdown label for a voice id, e.g. "Heart — US English, Female".</summary>
        private static string VoiceLabel(string id)
        {
            var v = VoiceCatalog.Get(id);
            return v != null ? v.DisplayName : (string.IsNullOrEmpty(id) ? "(none)" : id);
        }

        private static string VoiceGiven(string id) => VoiceCatalog.Get(id)?.GivenName ?? id;

        /// <summary>Open a FloatMenu of every available voice (optionally with a "None" entry).</summary>
        private static void OpenVoiceMenu(System.Action<string> onPick, bool includeNone)
        {
            var options = new List<FloatMenuOption>();
            if (includeNone)
                options.Add(new FloatMenuOption("None", () => onPick("")));
            foreach (var v in VoiceCatalog.All)
            {
                var captured = v;
                options.Add(new FloatMenuOption(captured.DisplayName, () => onPick(captured.Id)));
            }
            if (options.Count == 0)
                options.Add(new FloatMenuOption("(no voices installed — run download-assets.ps1)", null));
            Find.WindowStack.Add(new FloatMenu(options));
        }
    }
}
