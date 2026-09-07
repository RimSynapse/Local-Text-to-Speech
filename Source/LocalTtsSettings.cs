using Verse;

namespace RimSynapse.LocalTts
{
    public enum AccelerationMode
    {
        /// <summary>Use the GPU (DirectML) when an NVIDIA card is detected, otherwise CPU.</summary>
        Auto = 0,
        /// <summary>Always try the GPU (DirectML) first, fall back to CPU only on failure.</summary>
        Gpu = 1,
        /// <summary>Always run on CPU.</summary>
        Cpu = 2,
    }

    public class LocalTtsSettings : ModSettings
    {
        /// <summary>Kokoro voice id (matches a file in Resources/models/voices/&lt;id&gt;.bin).</summary>
        public string defaultVoice = "af_heart";

        /// <summary>
        /// Optional second voice to blend with (empty = none). Kokoro has no timbre knob; mixing two
        /// voices' style vectors is the way to reshape timbre. See <see cref="blendAmount"/>.
        /// </summary>
        public string blendVoice = "";

        /// <summary>Blend weight of <see cref="blendVoice"/> (0 = pure primary voice, 1 = pure blend voice).</summary>
        public float blendAmount = 0.5f;

        /// <summary>Speaking rate multiplier passed to the model (1.0 = normal).</summary>
        public float speed = 1.0f;

        /// <summary>Output gain applied to the synthesized waveform (1.0 = unchanged).</summary>
        public float volume = 1.0f;

        public AccelerationMode acceleration = AccelerationMode.Auto;

        /// <summary>Master enable — when false, Speak() calls are ignored.</summary>
        public bool enabled = true;

        /// <summary>Last phrase typed into the in-settings tester (persisted for convenience).</summary>
        public string testPhrase = "The storyteller watches, and the colony endures.";

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref defaultVoice, "defaultVoice", "af_heart");
            Scribe_Values.Look(ref blendVoice, "blendVoice", "");
            Scribe_Values.Look(ref blendAmount, "blendAmount", 0.5f);
            Scribe_Values.Look(ref speed, "speed", 1.0f);
            Scribe_Values.Look(ref volume, "volume", 1.0f);
            Scribe_Values.Look(ref acceleration, "acceleration", AccelerationMode.Auto);
            Scribe_Values.Look(ref enabled, "enabled", true);
            Scribe_Values.Look(ref testPhrase, "testPhrase", "The storyteller watches, and the colony endures.");
        }
    }
}
