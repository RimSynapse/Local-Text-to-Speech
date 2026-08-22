using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using RimSynapse;
using RimSynapse.Utils;

namespace RimSynapse.LocalTts
{
    /// <summary>
    /// Asynchronous Kokoro TTS pipeline. Requests are queued and processed on a single dedicated
    /// background thread — synthesis never touches Unity's main thread. Finished audio is handed to
    /// Core's <see cref="AudioPlaybackManager"/>, which marshals playback back onto the main thread.
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
            public float Speed;
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

        /// <summary>Estimated VRAM footprint of the loaded model in MB (0 until loaded / on CPU).</summary>
        public float EstimatedVramMb { get; private set; }

        /// <summary>Stable id used when registering this engine as a GPU-memory consumer with Core.</summary>
        private const string ConsumerModId = "rimsynapse.localtts";

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

        /// <summary>Queue text for synthesis + playback. Returns immediately.</summary>
        public void Speak(string text, string voice = null, float? speed = null)
        {
            var settings = LocalTtsMod.Instance?.Settings;
            if (settings != null && !settings.enabled)
            {
                SynapseLogger.Message("[LocalTTS] Speak() ignored — mod is disabled in settings.");
                return;
            }
            if (string.IsNullOrWhiteSpace(text)) return;

            Enqueue(new Request
            {
                Text = text,
                Voice = voice ?? settings?.defaultVoice ?? "af_heart",
                Speed = speed ?? settings?.speed ?? 1.0f,
            });
        }

        /// <summary>Synthesize without playing, returning raw float samples through a callback.</summary>
        public void Synthesize(string text, string voice, float speed, Action<float[]> onSamples)
        {
            Enqueue(new Request { Text = text, Voice = voice, Speed = speed, OnSamples = onSamples });
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
                    SynapseLogger.Error($"[LocalTTS] Synthesis error: {ex}");
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
                SynapseLogger.Warning($"[LocalTTS] {LastError} (looked for {TtsAssets.ModelPath})");
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

        /// <summary>
        /// Estimate the model's VRAM footprint and register it with Core's GPU-memory-consumers
        /// channel so a monitor mod (NVIDIA Tool) can surface it. When the session runs on CPU the
        /// consumer reports 0 (resident = false); RimWorld's DirectML allocation would otherwise be
        /// invisible inside the game's own process VRAM.
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

                RimSynapse.SynapseClient.Gpu?.UpsertConsumer(
                    ConsumerModId, "Local TTS (Kokoro)", EstimatedVramMb, _session.OnGpu);

                SynapseLogger.Message($"[LocalTTS] Registered VRAM consumer: ~{EstimatedVramMb:F0} MB " +
                                      $"({(_session.OnGpu ? "resident on GPU" : "CPU — not resident")}).");
            }
            catch (Exception ex)
            {
                SynapseLogger.Warning($"[LocalTTS] Failed to publish VRAM footprint: {ex.Message}");
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
            string phonemes = EspeakG2P.Phonemize(req.Text);
            if (string.IsNullOrEmpty(phonemes))
            {
                SynapseLogger.Warning($"[LocalTTS] No phonemes produced for: \"{Trim(req.Text)}\"");
                return;
            }

            var ids = KokoroTokenizer.Encode(phonemes);
            if (ids.Count == 0)
            {
                SynapseLogger.Warning("[LocalTTS] No in-vocabulary tokens produced.");
                return;
            }

            float[] style = VoiceStyleBank.GetStyle(req.Voice, ids.Count);
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
                SynapseLogger.Warning("[LocalTTS] Model returned no audio samples.");
                return;
            }

            float seconds = samples.Length / (float)PcmEncoder.SampleRate;
            SynapseLogger.Message($"[LocalTTS] Synthesized {seconds:F1}s in {ms}ms on {_session.ActiveProvider} " +
                                  $"({ids.Count} tokens, voice '{req.Voice}').");

            if (req.OnSamples != null)
            {
                req.OnSamples(samples);
                return;
            }

            byte[] pcm = PcmEncoder.FloatToPcm16(samples);
            AudioPlaybackManager.PlayPcm(pcm); // enqueues playback on the main thread internally
        }

        private static string Trim(string s) => s != null && s.Length > 60 ? s.Substring(0, 60) + "…" : s;

        public void Dispose()
        {
            _disposed = true;
            _queue.CompleteAdding();
            _session.Dispose();
            // Model is unloaded — stop reporting VRAM residency.
            try { RimSynapse.SynapseClient.Gpu?.UpsertConsumer(ConsumerModId, "Local TTS (Kokoro)", 0f, false); }
            catch { /* Core may already be torn down */ }
        }
    }
}
