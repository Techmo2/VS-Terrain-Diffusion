using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace VSTerrainDiffusion.Core;

/// <summary>NVIDIA: the GPU's name, compute capability and driver CUDA version, from NVML.</summary>
public readonly partial struct GpuInfo
{
    /// <summary>
    /// The NVIDIA Management Library, shipped with the display driver on Windows and Linux. Unlike
    /// the CUDA driver API it answers without creating a CUDA context.
    /// </summary>
    private static class Nvml
    {
        /// <summary>NVML_SUCCESS.</summary>
        private const int Success = 0;

        /// <summary>NVML_DEVICE_NAME_V2_BUFFER_SIZE.</summary>
        private const int NameBufferSize = 96;

        /// <summary>Most devices enumerated, as a guard.</summary>
        private const uint MaxDevices = 64;

        /// <summary>nvmlInit_v2.</summary>
        /// <returns>An nvmlReturn_t.</returns>
        private delegate int InitFn();

        /// <summary>nvmlShutdown.</summary>
        /// <returns>An nvmlReturn_t.</returns>
        private delegate int ShutdownFn();

        /// <summary>nvmlDeviceGetCount_v2.</summary>
        /// <param name="count">Receives the count.</param>
        /// <returns>An nvmlReturn_t.</returns>
        private delegate int GetCountFn(out uint count);

        /// <summary>nvmlDeviceGetHandleByIndex_v2.</summary>
        /// <param name="index">Device index.</param>
        /// <param name="device">Receives the handle.</param>
        /// <returns>An nvmlReturn_t.</returns>
        private delegate int GetHandleByIndexFn(uint index, out IntPtr device);

        /// <summary>nvmlDeviceGetName.</summary>
        /// <param name="device">The handle.</param>
        /// <param name="name">Receives the name, NUL-terminated UTF-8.</param>
        /// <param name="length">The buffer's size.</param>
        /// <returns>An nvmlReturn_t.</returns>
        private delegate int GetNameFn(IntPtr device, [Out] byte[] name, uint length);

        /// <summary>nvmlDeviceGetCudaComputeCapability.</summary>
        /// <param name="device">The handle.</param>
        /// <param name="major">Receives the major part.</param>
        /// <param name="minor">Receives the minor part.</param>
        /// <returns>An nvmlReturn_t.</returns>
        private delegate int GetComputeCapabilityFn(IntPtr device, out int major, out int minor);

        /// <summary>nvmlSystemGetCudaDriverVersion_v2.</summary>
        /// <param name="version">Receives the version, major * 1000 + minor * 10.</param>
        /// <returns>An nvmlReturn_t.</returns>
        private delegate int GetCudaDriverVersionFn(out int version);

        /// <summary>
        /// The GPU with the highest compute capability, which is the one CUDA numbers 0 by default
        /// (CUDA_DEVICE_ORDER=FASTEST_FIRST), or null without the driver or a GPU.
        /// </summary>
        public static NvidiaDevice Query()
        {
            if (!TryLoad(out IntPtr library)) return null;
            try
            {
                var init = Export<InitFn>(library, "nvmlInit_v2", "nvmlInit");
                var shutdown = Export<ShutdownFn>(library, "nvmlShutdown");
                if (init == null || shutdown == null || init() != Success) return null;
                try
                {
                    var getCount = Export<GetCountFn>(library, "nvmlDeviceGetCount_v2", "nvmlDeviceGetCount");
                    var getHandle = Export<GetHandleByIndexFn>(library, "nvmlDeviceGetHandleByIndex_v2", "nvmlDeviceGetHandleByIndex");
                    var getName = Export<GetNameFn>(library, "nvmlDeviceGetName");
                    var getComputeCapability = Export<GetComputeCapabilityFn>(library, "nvmlDeviceGetCudaComputeCapability");
                    var getCudaDriverVersion = Export<GetCudaDriverVersionFn>(library,
                        "nvmlSystemGetCudaDriverVersion_v2", "nvmlSystemGetCudaDriverVersion");
                    if (getCount == null || getHandle == null || getCount(out uint count) != Success || count == 0)
                        return null;

                    NvidiaDevice best = null;
                    for (uint i = 0; i < Math.Min(count, MaxDevices); i++)
                    {
                        // A device NVML cannot open (lost off the bus, no permission) is skipped, not fatal.
                        if (getHandle(i, out IntPtr device) != Success) continue;

                        var candidate = new NvidiaDevice();
                        var name = new byte[NameBufferSize];
                        if (getName?.Invoke(device, name, (uint)name.Length) == Success)
                        {
                            int length = Array.IndexOf(name, (byte)0);
                            candidate.Name = Encoding.UTF8.GetString(name, 0, length < 0 ? name.Length : length).Trim();
                        }
                        if (getComputeCapability?.Invoke(device, out int major, out int minor) == Success)
                            candidate.ComputeCapability = major * 10 + minor;

                        if (best == null || candidate.ComputeCapability > best.ComputeCapability) best = candidate;
                    }
                    if (best == null) return null;

                    if (getCudaDriverVersion?.Invoke(out int version) == Success)
                        best.CudaDriverVersion = version;
                    return best;
                }
                finally { shutdown(); }
            }
            catch { return null; }
            finally { NativeLibrary.Free(library); }
        }

        /// <summary>Loads NVML from where the driver puts it.</summary>
        /// <param name="library">Receives the handle.</param>
        /// <returns>Whether it loaded.</returns>
        private static bool TryLoad(out IntPtr library)
        {
            library = IntPtr.Zero;
            if (OperatingSystem.IsLinux()) return NativeLibrary.TryLoad("libnvidia-ml.so.1", out library);
            if (!OperatingSystem.IsWindows()) return false;

            // DCH drivers put it in System32; older ones only under NVSMI.
            return NativeLibrary.TryLoad("nvml.dll", out library) ||
                   NativeLibrary.TryLoad(Path.Combine(
                       Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                       "NVIDIA Corporation", "NVSMI", "nvml.dll"), out library);
        }
    }
}
