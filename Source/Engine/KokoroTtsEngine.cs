using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using RimSynapse;

namespace RimSynapse.LocalTts
{
    /// <summary>
    /// Asynchronous Kokoro TTS pipeline. Requests are queued and processed on a single dedicated
    /// background thread — synthesis never touches Unity's main thread. Finished audio is handed to
    /// <see cref="TtsAudioPlayer"/>, which marshals playback back onto the main thread.
    ///
    /// The heavy resources (native libs, ONNX session, espeak) are initialized lazily on the worker
    /// thread the first time a request is processed, so game load is never blocked.
    /// </summary>
    public sealed class KokoroTtsEngine : IDisposable
    {
        private sealed class Request
        {
            public string Text;
            public string Voice;
            public string BlendVoice;
            public float BlendAmount;
            public float Speed;
            public float Volume = 1f;
            public bool WarmupOnly;
            public Action<float[]> OnSamples; // optional raw-sample callback (for debug/inspection)
        }

        private readonly BlockingCollection<Request> _queue = new BlockingCollection<Request>(new ConcurrentQueue<Request>());
        private readonly KokoroSession _session = new KokoroSession();
        private Thread _worker;
        private volatile bool _sessionAttempted;
        private volatile bool _disposed;

        public bool Ready => _session.IsReady;
        public string ActiveProvider => _session.ActiveProvider;
        public string LastError { get; private set; }
        public int QueueDepth => _queue.Count;

        /// <summary>Estimated VRAM footprint of the loaded model in MB (0 until loaded).</summary>
        public float EstimatedVramMb { get; private set; }

        /// <summary>True when the model is actually resident on the GPU (vs CPU). Read by the NVIDIA Tool.</summary>
        public bool ResidentOnGpu => _session.OnGpu;

        public void Start()
        {
            if (_worker != null) return;
            _worker = new Thread(WorkerLoop)
            {
                Name = "RimSynapse-KokoroTTS",
                IsBackground = true,
            };
            _worker.Start();
        }

        /// <summary>
        /// Queue text for synthesis + playback using the current settings (voice, blend, speed,
        /// volume). Returns immediately.
        /// </summary>
        public void Speak(string text, string voice = null, float? speed = null)
        {
            var settings = LocalTtsMod.Instance?.Settings;
            if (settings != null && !settings.enabled)
            {
                TtsLog.Message("[LocalTTS] Speak() ignored — mod is disabled in settings.");
                return;
            }
            if (string.IsNullOrWhiteSpace(text)) return;

            Enqueue(new Request
            {
                Text = text,
                Voice = voice ?? settings?.defaultVoice ?? "af_heart",
                BlendVoice = settings?.blendVoice ?? "",
                BlendAmount = settings?.blendAmount ?? 0f,
                Speed = speed ?? settings?.speed ?? 1.0f,
                Volume = settings?.volume ?? 1f,
            });
        }

        /// <summary>Synthesize without playing, returning raw float samples through a callback.</summary>
        public void Synthesize(string text, string voice, float speed, Action<float[]> onSamples)
        {
            var settings = LocalTtsMod.Instance?.Settings;
            Enqueue(new Request
            {
                Text = text,
                Voice = voice,
                BlendVoice = settings?.blendVoice ?? "",
                BlendAmount = settings?.blendAmount ?? 0f,
                Speed = speed,
                Volume = settings?.volume ?? 1f,
                OnSamples = onSamples,
            });
        }

        /// <summary>Warm the engine (load natives + session) ahead of the first Speak.</summary>
        public void Warmup()
        {
            Enqueue(new Request { WarmupOnly = true });
        }

        private void Enqueue(Request r)
        {
            if (_disposed) return;
            Start();
            _queue.Add(r);
        }

        private void WorkerLoop()
        {
            foreach (var req in _queue.GetConsumingEnumerable())
            {
                if (_disposed) break;
                try
                {
                    if (!EnsureSession()) continue;
                    if (req.WarmupOnly)
                    {
                        // Also warm espeak so the first real request is fast.
                        EspeakG2P.EnsureInitialized();
                        continue;
                    }

                    ProcessRequest(req);
                }
                catch (Exception ex)
                {
                    LastError = ex.Message;
                    TtsLog.Error($"[LocalTTS] Synthesis error: {ex}");
                }
            }
        }

        private bool EnsureSession()
        {
            if (_session.IsReady) return true;
            if (_sessionAttempted && !_session.IsReady) return false; // don't spin on repeated failure

            _sessionAttempted = true;

            if (!TtsAssets.ModelInstalled)
            {
                LastError = "Model not installed. Run download-assets.ps1.";
                TtsLog.Warning($"[LocalTTS] {LastError} (looked for {TtsAssets.ModelPath})");
                return false;
            }

            NativeLibraryLoader.EnsureLoaded();
            if (!NativeLibraryLoader.Loaded)
            {
                LastError = "Native libraries failed to load.";
                return false;
            }

            bool preferGpu = ResolvePreferGpu();
            bool ok = _session.Load(TtsAssets.ModelPath, preferGpu);
            if (!ok) LastError = "ONNX session failed to load.";
            else PublishVramFootprint();
            return ok;
        }

        /// <summary>Stable id under which this mod registers its VRAM with Core's GpuStats channel.</summary>
        internal const string GpuConsumerModId = "rimsynapse.localtts";

        /// <summary>
        /// Estimate the model's VRAM footprint and register it with Core's in-process GPU-memory
        /// consumers channel (Core #104), so a monitor mod (the NVIDIA Tool) can give it its own
        /// VRAM breakdown line instead of lumping it into "System". On CPU the model isn't in VRAM,
        /// so it registers as non-resident (0 MB). <see cref="EstimatedVramMb"/> / <see cref="ResidentOnGpu"/>
        /// remain public for any consumer that still prefers to read them directly.
        /// </summary>
        private void PublishVramFootprint()
        {
            try
            {
                // Weights are uploaded to the device roughly at the model's on-disk size; add a
                // fixed allowance for the ORT session, arena, and intermediate activation buffers.
                float modelMb = new FileInfo(TtsAssets.ModelPath).Length / (1024f * 1024f);
                const float SessionOverheadMb = 64f;
                EstimatedVramMb = modelMb + SessionOverheadMb;

                TtsLog.Message($"[LocalTTS] Model VRAM footprint ~{EstimatedVramMb:F0} MB " +
                                      $"({(_session.OnGpu ? "resident on GPU" : "CPU — not resident")}).");
            }
            catch (Exception ex)
            {
                TtsLog.Warning($"[LocalTTS] Failed to estimate VRAM footprint: {ex.Message}");
            }

            // Register with Core's shared channel regardless of estimate outcome; a non-resident
            // (CPU) session reports 0 MB. Guarded so a Core without the channel can't break the engine.
            try
            {
                SynapseClient.Gpu?.UpsertConsumer(GpuConsumerModId, "Local TTS (Kokoro)", EstimatedVramMb, _session.OnGpu);
            }
            catch (Exception ex)
            {
                TtsLog.Warning($"[LocalTTS] Could not register GPU consumer with Core: {ex.Message}");
            }
        }

        private static bool ResolvePreferGpu()
        {
            var mode = LocalTtsMod.Instance?.Settings?.acceleration ?? AccelerationMode.Auto;
            switch (mode)
            {
                case AccelerationMode.Gpu: return true;
                case AccelerationMode.Cpu: return false;
                default: return GpuProbe.HasNvidia; // Auto
            }
        }

        private void ProcessRequest(Request req)
        {
            string lang = VoiceCatalog.EspeakLangFor(req.Voice);
            string phonemes = EspeakG2P.Phonemize(req.Text, lang);
            if (string.IsNullOrEmpty(phonemes))
            {
                TtsLog.Warning($"[LocalTTS] No phonemes produced for: \"{Trim(req.Text)}\"");
                return;
            }

            var ids = KokoroTokenizer.Encode(phonemes);
            if (ids.Count == 0)
            {
                TtsLog.Warning("[LocalTTS] No in-vocabulary tokens produced.");
                return;
            }

            float[] style = VoiceStyleBank.GetBlendedStyle(req.Voice, req.BlendVoice, req.BlendAmount, ids.Count);
            if (style == null)
            {
                LastError = $"Voice '{req.Voice}' unavailable.";
                return;
            }

            long t0 = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            float[] samples = _session.Run(ids, style, req.Speed);
            long ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - t0;

            if (samples == null || samples.Length == 0)
            {
                TtsLog.Warning("[LocalTTS] Model returned no audio samples.");
                return;
            }

            // Apply output gain.
            if (req.Volume != 1f && req.Volume > 0f)
            {
                for (int i = 0; i < samples.Length; i++)
                    samples[i] *= req.Volume;
            }

            float seconds = samples.Length / (float)PcmEncoder.SampleRate;
            TtsLog.Message($"[LocalTTS] Synthesized {seconds:F1}s in {ms}ms on {_session.ActiveProvider} " +
                                  $"({ids.Count} tokens, voice '{req.Voice}').");

            if (req.OnSamples != null)
            {
                req.OnSamples(samples);
                return;
            }

            // Play the raw samples directly through our own player (no PCM round-trip needed for
            // playback; PcmEncoder is still used when the broker stages a WAV file). The player
            // marshals the Unity AudioClip work onto the main thread itself.
            TtsAudioPlayer.Play(samples, PcmEncoder.SampleRate);
        }

        private static string Trim(string s) => s != null && s.Length > 60 ? s.Substring(0, 60) + "…" : s;

        public void Dispose()
        {
            _disposed = true;
            _queue.CompleteAdding();
            _session.Dispose();

            // Model is gone from VRAM — drop our row from Core's consumers channel (Core #104).
            try { SynapseClient.Gpu?.RemoveConsumer(GpuConsumerModId); }
            catch { /* Core without the channel; nothing to clean up */ }
        }
    }
}
