using System;
using System.Runtime.InteropServices;
using System.Text;

namespace RimSynapse.LocalTts
{
    /// <summary>
    /// Lightweight NVIDIA detection via NVML (nvml.dll ships with every NVIDIA driver).
    ///
    /// DirectML itself runs on any DirectX 12 GPU, so this is only used to (a) report the
    /// detected NVIDIA card in the settings UI and (b) drive the "Auto" acceleration policy,
    /// which prefers the GPU when an NVIDIA card is present. The actual GPU-vs-CPU decision
    /// still falls back to CPU at runtime if the DirectML provider fails to initialize.
    /// </summary>
    public static class GpuProbe
    {
        private const int NVML_SUCCESS = 0;

        // ── Quiet library probe (kernel32) ──
        // Resolving DllImport("nvml") through Mono on a machine without NVML makes the runtime sweep
        // every filename/path variant, emitting ~16 "Fallback handler could not load library" lines to
        // Player.log. We probe once with the Win32 loader — silent on failure — and only ever call an
        // nvml entry point when the library resolves. (Same technique as NVIDIA-Tool's NvidiaSmiReader.)
        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibrary(string lpFileName);

        [DllImport("kernel32", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool FreeLibrary(IntPtr hModule);

        [DllImport("nvml", EntryPoint = "nvmlInit_v2", CallingConvention = CallingConvention.Cdecl)]
        private static extern int nvmlInit();

        [DllImport("nvml", EntryPoint = "nvmlShutdown", CallingConvention = CallingConvention.Cdecl)]
        private static extern int nvmlShutdown();

        [DllImport("nvml", EntryPoint = "nvmlDeviceGetHandleByIndex_v2", CallingConvention = CallingConvention.Cdecl)]
        private static extern int nvmlDeviceGetHandleByIndex(uint index, out IntPtr device);

        [DllImport("nvml", EntryPoint = "nvmlDeviceGetName", CallingConvention = CallingConvention.Cdecl)]
        private static extern int nvmlDeviceGetName(IntPtr device, StringBuilder name, uint length);

        private static bool _probed;
        private static bool _hasNvidia;
        private static string _gpuName;

        /// <summary>True if at least one NVIDIA GPU was detected via NVML.</summary>
        public static bool HasNvidia { get { Probe(); return _hasNvidia; } }

        /// <summary>The detected NVIDIA GPU name, or null.</summary>
        public static string GpuName { get { Probe(); return _gpuName; } }

        /// <summary>
        /// Returns true if nvml.dll can be resolved by the Windows loader, using a plain Win32
        /// LoadLibrary probe so a missing library produces no Mono loader-failure spam.
        /// </summary>
        private static bool NvmlLibraryResolves()
        {
            try
            {
                IntPtr h = LoadLibrary("nvml.dll");
                if (h == IntPtr.Zero) return false;
                FreeLibrary(h);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void Probe()
        {
            if (_probed) return;
            _probed = true;

            // Gate every nvml P/Invoke behind a silent Win32 probe so machines without an NVIDIA
            // driver produce no loader-failure spam.
            if (!NvmlLibraryResolves())
                return;

            try
            {
                if (nvmlInit() != NVML_SUCCESS) return;
                try
                {
                    if (nvmlDeviceGetHandleByIndex(0, out IntPtr device) == NVML_SUCCESS)
                    {
                        var sb = new StringBuilder(96);
                        if (nvmlDeviceGetName(device, sb, (uint)sb.Capacity) == NVML_SUCCESS)
                        {
                            _gpuName = sb.ToString();
                            _hasNvidia = true;
                        }
                        else
                        {
                            _hasNvidia = true; // device exists even if the name query failed
                        }
                    }
                }
                finally
                {
                    nvmlShutdown();
                }
            }
            catch (DllNotFoundException)
            {
                // No NVIDIA driver present — nvml.dll not installed. Not an error.
            }
            catch (Exception)
            {
                // Any other failure: treat as "no NVIDIA".
            }
        }
    }
}
