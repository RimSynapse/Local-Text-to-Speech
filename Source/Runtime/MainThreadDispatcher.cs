using System;
using System.Collections.Concurrent;
using System.Threading;

namespace LocalTts
{
    /// <summary>
    /// Marshals work from the TTS worker thread onto Unity's main thread. Local TTS runs synthesis
    /// on a dedicated background thread but must touch Verse/Unity APIs — logging, audio playback,
    /// broker completion callbacks — on the main thread only. Actions posted from off-thread are
    /// queued and drained once per frame by <see cref="MainThreadPump"/>; an action posted while
    /// already on the main thread runs immediately.
    ///
    /// <para>This replaces the marshaling Local TTS previously borrowed from RimSynapse Core
    /// (SynapseLogger, AudioPlaybackManager), so the mod is self-sufficient with Core absent.</para>
    /// </summary>
    internal static class MainThreadDispatcher
    {
        private static readonly ConcurrentQueue<Action> _queue = new ConcurrentQueue<Action>();
        private static int _mainThreadId = -1;

        /// <summary>Record the main thread. Call once from the mod constructor (which runs on it).</summary>
        public static void CaptureMainThread() => _mainThreadId = Thread.CurrentThread.ManagedThreadId;

        /// <summary>True when the caller is on the captured main thread.</summary>
        public static bool OnMainThread => Thread.CurrentThread.ManagedThreadId == _mainThreadId;

        /// <summary>
        /// Run <paramref name="action"/> on the main thread — immediately if the caller is already
        /// there, otherwise on the next <see cref="Pump"/>. Actions posted before the pump is live
        /// (e.g. during early game load) are queued, not dropped, and flush on the first frame.
        /// </summary>
        public static void Post(Action action)
        {
            if (action == null) return;
            if (OnMainThread) { action(); return; }
            _queue.Enqueue(action);
        }

        /// <summary>Drain and run all queued actions. Main thread only — called by <see cref="MainThreadPump"/>.</summary>
        public static void Pump()
        {
            while (_queue.TryDequeue(out var action))
            {
                try { action(); }
                catch (Exception ex) { Verse.Log.Error("[LocalTTS] Main-thread action threw: " + ex); }
            }
        }
    }
}
