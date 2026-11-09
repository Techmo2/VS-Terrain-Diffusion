using System;
using System.Runtime.InteropServices;
using System.Text;

namespace VSTerrainDiffusion.Core;

/// <summary>Intel: whether Intel's own OpenCL runtime exposes a GPU, which OpenVINO's GPU plugin needs.</summary>
public readonly partial struct GpuInfo
{
    /// <summary>The OpenCL ICD loader, which lists every vendor runtime installed.</summary>
    private static class OpenCl
    {
        /// <summary>CL_SUCCESS.</summary>
        private const int Success = 0;

        /// <summary>CL_DEVICE_TYPE_GPU.</summary>
        private const ulong DeviceTypeGpu = 1 << 2;

        /// <summary>CL_PLATFORM_VENDOR.</summary>
        private const uint PlatformVendor = 0x0903;

        /// <summary>Most platforms enumerated, as a guard.</summary>
        private const uint MaxPlatforms = 64;

        /// <summary>clGetPlatformIDs.</summary>
        /// <param name="entries">Room in <paramref name="platforms"/>.</param>
        /// <param name="platforms">Receives the platforms, or null to count them.</param>
        /// <param name="count">Receives how many there are.</param>
        /// <returns>A cl_int status.</returns>
        private delegate int GetPlatformIdsFn(uint entries, [Out] IntPtr[] platforms, out uint count);

        /// <summary>clGetPlatformInfo.</summary>
        /// <param name="platform">The platform.</param>
        /// <param name="param">What to ask.</param>
        /// <param name="size">Room in <paramref name="value"/>.</param>
        /// <param name="value">Receives the answer.</param>
        /// <param name="sizeReturned">Receives its size.</param>
        /// <returns>A cl_int status.</returns>
        private delegate int GetPlatformInfoFn(IntPtr platform, uint param, nuint size, [Out] byte[] value, out nuint sizeReturned);

        /// <summary>clGetDeviceIDs.</summary>
        /// <param name="platform">The platform.</param>
        /// <param name="type">Device types wanted.</param>
        /// <param name="entries">Room in <paramref name="devices"/>.</param>
        /// <param name="devices">Receives the devices, or null to count them.</param>
        /// <param name="count">Receives how many there are.</param>
        /// <returns>A cl_int status.</returns>
        private delegate int GetDeviceIdsFn(IntPtr platform, ulong type, uint entries, [Out] IntPtr[] devices, out uint count);

        /// <summary>
        /// Whether Intel's own runtime exposes a GPU. Mesa's rusticl can list the same GPU under
        /// its own platform, which OpenVINO cannot use, so the platform vendor is checked too.
        /// </summary>
        public static bool HasIntelGpu()
        {
            // The loader is never freed: unloading it unloads the vendor ICDs it opened, and not
            // every ICD survives that.
            string libraryName = OperatingSystem.IsWindows() ? "OpenCL.dll" : "libOpenCL.so.1";
            if (!NativeLibrary.TryLoad(libraryName, out IntPtr library)) return false;

            var getPlatformIds = Export<GetPlatformIdsFn>(library, "clGetPlatformIDs");
            var getPlatformInfo = Export<GetPlatformInfoFn>(library, "clGetPlatformInfo");
            var getDeviceIds = Export<GetDeviceIdsFn>(library, "clGetDeviceIDs");
            if (getPlatformIds == null || getPlatformInfo == null || getDeviceIds == null) return false;

            if (getPlatformIds(0, null, out uint count) != Success || count == 0) return false;
            var platforms = new IntPtr[Math.Min(count, MaxPlatforms)];
            if (getPlatformIds((uint)platforms.Length, platforms, out _) != Success) return false;

            foreach (IntPtr platform in platforms)
            {
                var vendor = new byte[256];
                if (getPlatformInfo(platform, PlatformVendor, (nuint)vendor.Length, vendor, out _) != Success) continue;
                int length = Array.IndexOf(vendor, (byte)0);
                if (!Encoding.ASCII.GetString(vendor, 0, length < 0 ? vendor.Length : length)
                        .Contains("Intel", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (getDeviceIds(platform, DeviceTypeGpu, 0, null, out uint devices) == Success && devices > 0)
                    return true;
            }
            return false;
        }
    }
}
