using System;
using System.Linq;
using System.Reflection;

namespace RimSynapse.LocalTts
{
    /// <summary>
    /// Optional soft integration with RimSynapse Core's GPU-memory stats channel (Core #104),
    /// bound entirely by reflection so Local TTS carries no compile-time dependency on Core. When
    /// Core is loaded, the mod's VRAM footprint is reported to <c>RimSynapse.SynapseClient.Gpu</c>
    /// so a monitor mod (the NVIDIA Tool) can give it its own breakdown line; when Core is absent,
    /// every call is a silent no-op. This is the last of Core's runtime hooks to move behind
    /// reflection, and the reason Local TTS can run standalone.
    /// </summary>
    internal static class CoreGpuBridge
    {
        private static bool _resolved;
        private static PropertyInfo _gpuProp; // static SynapseClient.Gpu -> GpuStats
        private static MethodInfo _upsert;    // GpuStats.UpsertConsumer(string, string, float, bool)
        private static MethodInfo _remove;    // GpuStats.RemoveConsumer(string)

        private static void Resolve()
        {
            if (_resolved) return;
            _resolved = true;
            try
            {
                Type client = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try { client = asm.GetType("RimSynapse.SynapseClient", false); }
                    catch { client = null; }
                    if (client != null) break;
                }
                if (client == null) return; // Core not present — stay a no-op

                _gpuProp = client.GetProperty("Gpu", BindingFlags.Public | BindingFlags.Static);
                Type gpuType = _gpuProp?.PropertyType;
                if (gpuType == null) return;

                _upsert = gpuType.GetMethod("UpsertConsumer",
                    new[] { typeof(string), typeof(string), typeof(float), typeof(bool) });
                _remove = gpuType.GetMethod("RemoveConsumer", new[] { typeof(string) });
            }
            catch
            {
                // Any resolution failure leaves the method handles null → every call no-ops.
            }
        }

        /// <summary>Report (or update) this mod's VRAM row on Core's channel. No-op without Core.</summary>
        public static void UpsertConsumer(string modId, string label, float vramMb, bool resident)
        {
            Resolve();
            try
            {
                object gpu = _gpuProp?.GetValue(null);
                if (gpu != null && _upsert != null)
                    _upsert.Invoke(gpu, new object[] { modId, label, vramMb, resident });
            }
            catch
            {
                // Core present but the channel misbehaved — never let it break synthesis.
            }
        }

        /// <summary>Drop this mod's VRAM row from Core's channel. No-op without Core.</summary>
        public static void RemoveConsumer(string modId)
        {
            Resolve();
            try
            {
                object gpu = _gpuProp?.GetValue(null);
                if (gpu != null && _remove != null)
                    _remove.Invoke(gpu, new object[] { modId });
            }
            catch
            {
                // Nothing to clean up if the channel is gone.
            }
        }
    }
}
