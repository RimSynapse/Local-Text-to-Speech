using UnityEngine;

namespace LocalTts
{
    /// <summary>
    /// A persistent Unity component that drains <see cref="MainThreadDispatcher"/> once per frame,
    /// in both the main menu and in-game. It is created on a <c>DontDestroyOnLoad</c> GameObject
    /// from the mod constructor, so the pump needs no active Game and no Harmony patch to run — it
    /// starts ticking as soon as Unity begins updating, and survives save/load and scene changes.
    /// </summary>
    public sealed class MainThreadPump : MonoBehaviour
    {
        private void Update()
        {
            MainThreadDispatcher.Pump();
            TtsAudioPlayer.Tick();
        }
    }
}
