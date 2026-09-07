using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace LocalTts
{
    /// <summary>
    /// The public, reflection-friendly entry point any mod uses to get speech from Local TTS
    /// without referencing this assembly. A caller hands over text (and, optionally, a voice, blend,
    /// speed and volume), gets a ticket id back immediately, and either polls
    /// (<see cref="GetStatus"/>/<see cref="GetResultPath"/>) or registers a callback
    /// (<see cref="OnComplete"/>). Synthesis runs on Local TTS's worker thread; the finished audio is
    /// staged as a 16-bit PCM WAV in a hash-keyed cache, so an identical request is an instant hit.
    /// Readiness is strict — a ticket flips to <see cref="STATUS_READY"/> only after the file is fully
    /// written — which makes it a safe speech-gating signal (issue #8).
    ///
    /// <para><b>Voice</b> is a single flexible string: empty for the mod's default voice, a bundled
    /// voice id (e.g. <c>"af_heart"</c>), or a filesystem path to a caller-supplied Kokoro <c>.bin</c>
    /// style vector (their own designed voice or an external export). <c>blendVoice</c> accepts the
    /// same forms. Everything beyond <c>text</c> is optional via overloads: the simplest call is
    /// <c>RequestSpeech(text)</c> or <c>PlaySpeech(text)</c>.</para>
    /// </summary>
    public static class LocalTtsBroker
    {
        public const int STATUS_PENDING = 0;
        public const int STATUS_READY = 1;
        public const int STATUS_FAILED = 2;

        private sealed class Ticket
        {
            public int Status = STATUS_PENDING;
            public string Path;
            public string Error;
            public long LastAccess;
            public bool Owned; // this process kicked the synthesis (vs. deduped onto an existing one)
            public readonly List<Action<string, string>> Callbacks = new List<Action<string, string>>();
        }

        private static readonly ConcurrentDictionary<string, Ticket> _tickets =
            new ConcurrentDictionary<string, Ticket>();

        /// <summary>Root for the WAV cache; set once from the mod ctor (main thread).</summary>
        internal static string CacheRoot;
        private static string _cacheDir;

        private static long Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        private static string CacheDir
        {
            get
            {
                if (_cacheDir == null)
                {
                    string root = CacheRoot ?? Path.GetTempPath();
                    _cacheDir = Path.Combine(root, "LocalTTS", "cache");
                    try { Directory.CreateDirectory(_cacheDir); }
                    catch (Exception ex) { TtsLog.Warning($"[LocalTTS] Could not create cache dir: {ex.Message}"); }
                }
                return _cacheDir;
            }
        }

        // ───────────────────────── public API ─────────────────────────

        /// <summary>Simplest form: default voice, normal speed and volume. Returns a ticket id (null if unavailable).</summary>
        public static string RequestSpeech(string text)
            => RequestInternal(text, null, null, 0f, 1f, 1f, play: false);

        /// <summary>Request with a voice (bundled id or path), speed and volume.</summary>
        public static string RequestSpeech(string text, string voice, float speed, float volume)
            => RequestInternal(text, voice, null, 0f, speed, volume, play: false);

        /// <summary>Full control: blend two voices (each a bundled id or path) by <paramref name="blendAmount"/> (0..1).</summary>
        public static string RequestSpeech(string text, string voice, string blendVoice, float blendAmount,
                                           float speed, float volume)
            => RequestInternal(text, voice, blendVoice, blendAmount, speed, volume, play: false);

        /// <summary>Fire-and-forget: synthesize and play through Local TTS's own audio, default voice.</summary>
        public static void PlaySpeech(string text)
            => RequestInternal(text, null, null, 0f, 1f, 1f, play: true);

        /// <summary>Fire-and-forget with a voice, speed and volume.</summary>
        public static void PlaySpeech(string text, string voice, float speed, float volume)
            => RequestInternal(text, voice, null, 0f, speed, volume, play: true);

        /// <summary>Ticket status: 0 pending, 1 ready, 2 failed (also 2 for an unknown ticket).</summary>
        public static int GetStatus(string ticketId)
        {
            if (ticketId != null && _tickets.TryGetValue(ticketId, out var t))
                lock (t) { t.LastAccess = Now; return t.Status; }
            return STATUS_FAILED;
        }

        /// <summary>The staged WAV path once ready, otherwise null.</summary>
        public static string GetResultPath(string ticketId)
        {
            if (ticketId != null && _tickets.TryGetValue(ticketId, out var t))
                lock (t) { return t.Status == STATUS_READY ? t.Path : null; }
            return null;
        }

        /// <summary>The failure reason if the ticket failed, otherwise null.</summary>
        public static string GetError(string ticketId)
        {
            if (ticketId != null && _tickets.TryGetValue(ticketId, out var t))
                lock (t) { return t.Error; }
            return "unknown ticket";
        }

        /// <summary>
        /// Register a completion callback for a ticket. Fires as <c>(ticketId, path)</c> — path is the
        /// staged WAV on success, null on failure (check <see cref="GetError"/>). Always invoked on the
        /// main thread. If the ticket is already finished, fires on the next frame. Returns false for
        /// an unknown ticket. Intended for mods that reference Local TTS directly; reflection callers
        /// use the polling API.
        /// </summary>
        public static bool OnComplete(string ticketId, Action<string, string> callback)
        {
            if (callback == null || ticketId == null) return false;
            if (!_tickets.TryGetValue(ticketId, out var t)) return false;

            bool fireNow = false;
            string path = null;
            lock (t)
            {
                if (t.Status == STATUS_PENDING) t.Callbacks.Add(callback);
                else { fireNow = true; path = t.Status == STATUS_READY ? t.Path : null; }
            }
            if (fireNow) PostCallback(callback, ticketId, path);
            return true;
        }

        // ───────────────────────── internals ─────────────────────────

        private static string RequestInternal(string text, string voice, string blendVoice,
                                              float blendAmount, float speed, float volume, bool play)
        {
            var mod = LocalTtsMod.Instance;
            var engine = mod?.Engine;
            var settings = mod?.Settings;
            if (engine == null || settings == null || !settings.enabled) return null;
            if (string.IsNullOrWhiteSpace(text)) return null;

            if (speed <= 0f) speed = 1f;
            if (volume <= 0f) volume = 1f;
            string resolvedVoice = string.IsNullOrEmpty(voice) ? settings.defaultVoice : voice;

            string key = Key(text, resolvedVoice, blendVoice, blendAmount, speed, volume);
            string wavPath = Path.Combine(CacheDir, key + ".wav");

            // Cache hit — the file is already staged and playable.
            if (File.Exists(wavPath))
            {
                var hit = _tickets.GetOrAdd(key, _ => new Ticket());
                lock (hit) { hit.Status = STATUS_READY; hit.Path = wavPath; hit.LastAccess = Now; }
                Touch(wavPath);
                if (play) PlayCachedFile(wavPath);
                return key;
            }

            // First requester of this exact synthesis owns it; concurrent duplicates dedupe onto it.
            var ticket = new Ticket { Status = STATUS_PENDING, LastAccess = Now, Owned = true };
            if (!_tickets.TryAdd(key, ticket))
            {
                var existing = _tickets[key];
                lock (existing)
                {
                    existing.LastAccess = Now;
                    if (existing.Status == STATUS_READY && play && existing.Path != null)
                        PlayCachedFile(existing.Path);
                }
                return key;
            }

            engine.Render(text, resolvedVoice, blendVoice, blendAmount, speed, volume, (samples, error) =>
            {
                // Worker thread. File IO is fine here; playback + callbacks marshal to the main thread.
                if (samples == null)
                {
                    Complete(ticket, key, STATUS_FAILED, null, error ?? "synthesis failed");
                    return;
                }
                if (!WavWriter.WriteAtomic(samples, PcmEncoder.SampleRate, wavPath))
                {
                    Complete(ticket, key, STATUS_FAILED, null, "failed to stage audio file");
                    return;
                }
                if (play) TtsAudioPlayer.Play(samples, PcmEncoder.SampleRate);
                Complete(ticket, key, STATUS_READY, wavPath, null);
                PruneCache();
            });

            return key;
        }

        private static void Complete(Ticket t, string key, int status, string path, string error)
        {
            Action<string, string>[] callbacks;
            lock (t)
            {
                t.Status = status;
                t.Path = path;
                t.Error = error;
                t.LastAccess = Now;
                callbacks = t.Callbacks.ToArray();
                t.Callbacks.Clear();
            }
            foreach (var cb in callbacks)
                PostCallback(cb, key, status == STATUS_READY ? path : null);

            PruneTickets();
        }

        private static void PostCallback(Action<string, string> cb, string ticketId, string path)
        {
            MainThreadDispatcher.Post(() =>
            {
                try { cb(ticketId, path); }
                catch (Exception ex) { TtsLog.Error($"[LocalTTS] broker callback threw: {ex.Message}"); }
            });
        }

        private static void PlayCachedFile(string wavPath)
        {
            if (WavWriter.TryRead(wavPath, out float[] samples, out int rate) && samples != null)
                TtsAudioPlayer.Play(samples, rate);
        }

        /// <summary>Stable cache key for a request — identical requests collapse to one file.</summary>
        private static string Key(string text, string voice, string blendVoice, float blendAmount,
                                  float speed, float volume)
        {
            string raw = string.Join("", new[]
            {
                text ?? "",
                voice ?? "",
                blendVoice ?? "",
                blendAmount.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                speed.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                volume.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            });
            using (var sha = SHA1.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(raw));
                var sb = new StringBuilder(hash.Length * 2);
                foreach (byte b in hash) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        private static void Touch(string path)
        {
            try { File.SetLastWriteTimeUtc(path, DateTime.UtcNow); } catch { /* best-effort LRU timestamp */ }
        }

        /// <summary>Evict oldest cached WAVs (by last-write time) once the cache exceeds the size cap.</summary>
        private static void PruneCache()
        {
            try
            {
                long capBytes = Math.Max(16, LocalTtsMod.Instance?.Settings?.cacheCapMb ?? 250) * 1024L * 1024L;
                var files = new DirectoryInfo(CacheDir).GetFiles("*.wav");
                long total = files.Sum(f => f.Length);
                if (total <= capBytes) return;

                foreach (var f in files.OrderBy(f => f.LastWriteTimeUtc))
                {
                    if (total <= capBytes) break;
                    long len = f.Length;
                    try { f.Delete(); total -= len; }
                    catch { /* in use or gone; skip */ }
                }
            }
            catch (Exception ex)
            {
                TtsLog.Warning($"[LocalTTS] Cache prune failed: {ex.Message}");
            }
        }

        /// <summary>Cap in-memory ticket metadata; drop the oldest finished tickets past a soft limit.</summary>
        private static void PruneTickets()
        {
            const int Cap = 256;
            if (_tickets.Count <= Cap) return;
            foreach (var kv in _tickets.Where(kv => kv.Value.Status != STATUS_PENDING)
                                       .OrderBy(kv => kv.Value.LastAccess)
                                       .Take(_tickets.Count - Cap))
                _tickets.TryRemove(kv.Key, out _);
        }
    }
}
