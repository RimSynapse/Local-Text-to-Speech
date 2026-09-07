using System.Collections.Generic;
using UnityEngine;

namespace RimSynapse.LocalTts
{
    /// <summary>
    /// Local TTS's own audio output. Replaces the dependency on RimSynapse Core's
    /// AudioPlaybackManager: synthesized samples (mono float, 24 kHz) are turned into a Unity
    /// <see cref="AudioClip"/> and played through a single persistent 2D <see cref="AudioSource"/>
    /// on the mod's DontDestroyOnLoad host object.
    ///
    /// <para>All Unity calls happen on the main thread — the worker thread reaches this only via
    /// <see cref="MainThreadDispatcher.Post"/>. Clips are queued and played in order (a new line
    /// waits for the current one rather than cutting it off); <see cref="Tick"/>, driven by
    /// <see cref="MainThreadPump"/>, advances the queue. Output gain is already baked into the
    /// samples by the engine, so the source runs at unit volume.</para>
    /// </summary>
    internal static class TtsAudioPlayer
    {
        private static AudioSource _source;
        private static readonly Queue<AudioClip> _pending = new Queue<AudioClip>();

        /// <summary>Attach the playback source to the mod's persistent host object. Main thread.</summary>
        public static void Init(GameObject host)
        {
            _source = host.AddComponent<AudioSource>();
            _source.playOnAwake = false;
            _source.loop = false;
            _source.spatialBlend = 0f; // 2D — UI/narration audio, not positional
            _source.volume = 1f;
        }

        /// <summary>
        /// Queue mono <paramref name="samples"/> at <paramref name="sampleRate"/> for playback.
        /// Safe to call from the worker thread; the clip is built and enqueued on the main thread.
        /// </summary>
        public static void Play(float[] samples, int sampleRate)
        {
            if (samples == null || samples.Length == 0) return;
            MainThreadDispatcher.Post(() =>
            {
                var clip = AudioClip.Create("LocalTTS", samples.Length, 1, sampleRate, false);
                clip.SetData(samples, 0);
                _pending.Enqueue(clip);
            });
        }

        /// <summary>Stop playback and drop anything queued. Safe to call from any thread.</summary>
        public static void Stop()
        {
            MainThreadDispatcher.Post(() =>
            {
                _pending.Clear();
                if (_source != null) _source.Stop();
            });
        }

        /// <summary>Advance the queue: start the next clip once the source is idle. Main thread only.</summary>
        public static void Tick()
        {
            if (_source == null || _source.isPlaying) return;
            if (_pending.Count == 0) return;
            _source.clip = _pending.Dequeue();
            _source.Play();
        }
    }
}
