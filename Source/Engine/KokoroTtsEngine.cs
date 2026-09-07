using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;

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
            // Optional result sink: (samples, error). On success error is null; on failure samples
            // is null and error carries the reason (so the broker can surface it, never hang a ticket).
            public Action<float[], string> OnResult;
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
                OnResult = (samples, _) => onSamples?.Invoke(samples),
            });
        }

        /// <summary>
        /// Full-spec synthesis for the broker: renders <paramref name="text"/> with an explicit
        /// voice/blend/speed/volume and delivers the finished mono samples (gain already applied)
        /// through <paramref name="onResult"/> — <c>(samples, null)</c> on success, or
        /// <c>(null, reason)</c> on failure. Does not play; the broker stages a file and/or plays.
        /// An empty <paramref name="voice"/> falls back to the configured default voice.
        /// </summary>
        public void Render(string text, string voice, string blendVoice, float blendAmount,
                           float speed, float volume, Action<float[], string> onResult)
        {
            if (string.IsNullOrWhiteSpace(text)) { onResult?.Invoke(null, "empty text"); return; }
            string defaultVoice = LocalTtsMod.Instance?.Settings?.defaultVoice ?? "af_heart";
            Enqueue(new Request
            {
                Text = text,
                Voice = string.IsNullOrEmpty(voice) ? defaultVoice : voice,
                BlendVoice = blendVoice ?? "",
                BlendAmount = blendAmount,
                Speed = speed <= 0f ? 1f : speed,
                Volume = volume <= 0f ? 1f : volume,
                OnResult = onResult,
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
                    try { req.OnResult?.Invoke(null, ex.Message); } catch { /* sink threw; ignore */ }
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

            // Report to Core's shared channel if Core is loaded — via reflection, so this is a
            // no-op when Core is absent (Local TTS is standalone). A non-resident (CPU) session
            // reports 0 MB. The bridge swallows its own failures; it can never break the engine.
            CoreGpuBridge.UpsertConsumer(GpuConsumerModId, "Local TTS (Kokoro)", EstimatedVramMb, _session.OnGpu);
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
            long t0 = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            float[] samples = RunPipeline(req, out string error);
            long ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - t0;

            if (samples == null)
            {
                LastError = error;
                TtsLog.Warning($"[LocalTTS] {error} (voice '{req.Voice}')");
                req.OnResult?.Invoke(null, error); // never leave a broker ticket hanging
                return;
            }

            float seconds = samples.Length / (float)PcmEncoder.SampleRate;
            TtsLog.Message($"[LocalTTS] Synthesized {seconds:F1}s in {ms}ms on {_session.ActiveProvider} " +
                                  $"(voice '{req.Voice}').");

            // A result sink (broker/debug) takes the samples; otherwise this is a plain Speak → play.
            if (req.OnResult != null)
                req.OnResult(samples, null);
            else
                TtsAudioPlayer.Play(samples, PcmEncoder.SampleRate);
        }

        /// <summary>
        /// The synthesis pipeline: text → espeak phonemes → tokens → blended style → ONNX → mono
        /// float samples with output gain applied. Returns null and sets <paramref name="error"/>
        /// on any stage failure, so every caller path can report rather than fall silent.
        /// </summary>
        private float[] RunPipeline(Request req, out string error)
        {
            error = null;

            string lang = VoiceCatalog.EspeakLangFor(req.Voice);
            string phonemes = EspeakG2P.Phonemize(req.Text, lang);
            if (string.IsNullOrEmpty(phonemes)) { error = $"No phonemes produced for \"{Trim(req.Text)}\""; return null; }

            var ids = KokoroTokenizer.Encode(phonemes);
            if (ids.Count == 0) { error = "No in-vocabulary tokens produced"; return null; }

            float[] style = VoiceStyleBank.GetBlendedStyle(req.Voice, req.BlendVoice, req.BlendAmount, ids.Count);
            if (style == null) { error = $"Voice '{req.Voice}' unavailable"; return null; }

            float[] samples = _session.Run(ids, style, req.Speed);
            if (samples == null || samples.Length == 0) { error = "Model returned no audio samples"; return null; }

            if (req.Volume != 1f && req.Volume > 0f)
                for (int i = 0; i < samples.Length; i++) samples[i] *= req.Volume;

            return samples;
        }

        private static string Trim(string s) => s != null && s.Length > 60 ? s.Substring(0, 60) + "…" : s;

        public void Dispose()
        {
            _disposed = true;
            _queue.CompleteAdding();
            _session.Dispose();

            // Model is gone from VRAM — drop our row from Core's consumers channel if Core is
            // present (reflection; no-op otherwise).
            CoreGpuBridge.RemoveConsumer(GpuConsumerModId);
        }
    }
}
