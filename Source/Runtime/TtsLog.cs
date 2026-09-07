namespace LocalTts
{
    /// <summary>
    /// Local TTS's internal logger. Replaces the dependency on RimSynapse Core's SynapseLogger:
    /// messages are forwarded to Verse's log on the main thread, marshaling from the synthesis
    /// worker thread via <see cref="MainThreadDispatcher"/>. Verse's <c>Log</c> is not thread-safe,
    /// so this keeps worker-thread diagnostics both safe and (unlike the old path) never dropped
    /// during early load — they queue and flush on the first frame.
    /// </summary>
    internal static class TtsLog
    {
        public static void Message(string msg) => MainThreadDispatcher.Post(() => Verse.Log.Message(msg));
        public static void Warning(string msg) => MainThreadDispatcher.Post(() => Verse.Log.Warning(msg));
        public static void Error(string msg) => MainThreadDispatcher.Post(() => Verse.Log.Error(msg));
    }
}
