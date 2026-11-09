using System;
using System.Runtime.InteropServices;

namespace VSTerrainDiffusion.Core;

/// <summary>Each inference runtime's requirements of the GPU, driver and operating system.</summary>
public readonly partial struct GpuInfo
{
    /// <summary>
    /// Whether the GPU runs a CUDA major version: an NVIDIA GPU with the proprietary driver, new enough for
    /// both.
    /// </summary>
    /// <param name="manufacturer">The GPU's maker.</param>
    /// <param name="nvidia">What NVML said, or null without the driver.</param>
    /// <param name="cudaMajor">12 or 13.</param>
    /// <returns>Whether it runs.</returns>
    private static bool DoesSupportCuda(GpuManufacturer manufacturer, NvidiaDevice nvidia, int cudaMajor)
    {
        // AMD GPUs go through ROCm instead (AmdGfxTarget). No NVML means no proprietary driver
        // (nouveau, or none), and CUDA needs that driver.
        if (manufacturer != GpuManufacturer.Nvidia || nvidia == null)
            return false;

        // CUDA 12 dropped Kepler; CUDA 13 dropped Maxwell, Pascal and Volta.
        return cudaMajor switch
        {
            12 => nvidia.ComputeCapability >= 50 && nvidia.CudaDriverVersion >= 12000,
            13 => nvidia.ComputeCapability >= 75 && nvidia.CudaDriverVersion >= 13000,
            _ => false
        };
    }

    /// <summary>
    /// Whether DirectML runs: Windows 10 1903 or later and a Direct3D 12 device at feature level 11_0.
    /// </summary>
    /// <returns>Whether it runs.</returns>
    private static bool DoesSupportDirectMl()
    {
        // DirectML is only supported on Windows, from Windows 10 1903
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 18362))
            return false;

        // DirectML runs on any vendor's GPU with a Direct3D 12 driver at feature level 11_0.
        return Dxgi.SupportsDirect3D12();
    }

    /// <summary>
    /// Whether OpenVINO's GPU plugin could run: a Gen9 or newer Intel GPU on 64-bit Windows or Linux with
    /// Intel's OpenCL runtime.
    /// </summary>
    /// <param name="manufacturer">The GPU's maker.</param>
    /// <param name="adapter">The adapter.</param>
    /// <returns>Whether it could.</returns>
    private static bool DoesSupportOpenVino(GpuManufacturer manufacturer, Adapter adapter)
    {
        // OpenVINO is only supported on Intel GPUs and CPUs.
        // Technically, it also supports some weird shit like FPGAs, but let's limit the magnitude of our migraine for now.
        if (manufacturer != GpuManufacturer.Intel)
            return false;

        if (!(OperatingSystem.IsWindows() || OperatingSystem.IsLinux()) ||
            RuntimeInformation.OSArchitecture != Architecture.X64)
            return false;

        // The GPU plugin needs Gen9 (Skylake) or newer. Without a device id there is no telling.
        if (adapter.PciDeviceId == 0 || IsPreGen9IntelGpu(adapter.PciDeviceId))
            return false;

        // It runs on Intel's OpenCL runtime: part of the graphics driver on Windows, the separate
        // compute-runtime (NEO) package on Linux. Without it OpenVINO sees no GPU at all.
        return OpenCl.HasIntelGpu();
    }

    /// <summary>
    /// Intel's GPUs before Gen9, by PCI device id (from the kernel's i915_pciids.h). Listing the old
    /// parts rather than the new ones means a GPU released after this was written is let through.
    /// </summary>
    private static bool IsPreGen9IntelGpu(int deviceId) =>
        deviceId is >= 0x0040 and <= 0x004F      // Ironlake
            or >= 0x0100 and <= 0x016F           // Sandy Bridge, Ivy Bridge, Valleyview
            or >= 0x0400 and <= 0x04FF           // Haswell
            or >= 0x0A00 and <= 0x0AFF           // Haswell ULT
            or >= 0x0BE0 and <= 0x0BEF           // Cedarview (0x0BD0-0x0BDB is Ponte Vecchio, which is Gen12)
            or >= 0x0C00 and <= 0x0CFF           // Haswell SDV
            or >= 0x0D00 and <= 0x0DFF           // Haswell CRW
            or >= 0x0F30 and <= 0x0F3F           // Bay Trail
            or >= 0x1600 and <= 0x16FF           // Broadwell
            or >= 0x22B0 and <= 0x22BF           // Cherry View, Braswell
            or >= 0x2500 and <= 0x2FFF           // i845 through GM45
            or >= 0x3570 and <= 0x358F           // i830, i855, i865
            or >= 0x7120 and <= 0x712F           // i810
            or 0x1132 or 0x7800 or 0x4100        // i815, i740, Moorestown
            or >= 0x8100 and <= 0x810F           // Poulsbo
            or >= 0xA000 and <= 0xA01F;          // Pineview

    /// <summary>
    /// TensorRT RTX's supported GPUs, which are also the targets of its default engine: Ampere GA10x (8.6),
    /// Ada (8.9) and consumer Blackwell (12.0, 12.1). Turing (7.5) is supported but left out of the default
    /// engine, and datacenter parts (8.0, 9.0, 10.x) are not supported at all.
    /// </summary>
    private static readonly int[] TensorRtRtxComputeCapabilities = { 86, 89, 120, 121 };

    /// <summary>
    /// Whether the TensorRT RTX plugin runs: one of <see cref="TensorRtRtxComputeCapabilities"/> on 64-bit
    /// Windows or Linux, with a CUDA 13 driver.
    /// </summary>
    /// <param name="manufacturer">The GPU's maker.</param>
    /// <param name="nvidia">What NVML said, or null.</param>
    /// <returns>Whether it runs.</returns>
    private static bool DoesSupportTensorrtRtx(GpuManufacturer manufacturer, NvidiaDevice nvidia)
    {
        // TensorRT RTX is only supported on Nvidia GPUs.
        if (manufacturer != GpuManufacturer.Nvidia || nvidia == null)
            return false;

        if (!(OperatingSystem.IsWindows() || OperatingSystem.IsLinux()) ||
            RuntimeInformation.OSArchitecture != Architecture.X64)
            return false;

        // The pinned plugin is the cu13 build, which needs a CUDA 13 (580+) driver.
        return Array.IndexOf(TensorRtRtxComputeCapabilities, nvidia.ComputeCapability) >= 0 &&
               nvidia.CudaDriverVersion >= 13000;
    }

    /// <summary>Whether Core ML runs: any Mac on macOS 10.15 or later.</summary>
    /// <returns>Whether it runs.</returns>
    private static bool DoesSupportCoreMl()
    {
        // CoreML is only supported on Apple Systems. It runs on every Mac: the GPU and Neural Engine
        // on Apple silicon, the GPU on an Intel Mac. ONNX Runtime's provider needs macOS 10.15.
        return OperatingSystem.IsMacOSVersionAtLeast(10, 15);
    }
}
