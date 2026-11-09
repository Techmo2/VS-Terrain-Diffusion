using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace VSTerrainDiffusion.Core;

/// <summary>
/// What this machine's GPU and operating system can theoretically run: which inference runtimes,
/// and on which device. Detection never throws: anything it cannot establish comes back as
/// unsupported.
///
/// Split by source: this file decides which adapter counts and assembles the answer;
/// <c>GpuInfo.Support.cs</c> holds each runtime's requirements, and the remaining files ask one
/// platform or driver API each (Linux sysfs, macOS system_profiler, DXGI, NVML, OpenCL).
/// </summary>
public readonly partial struct GpuInfo
{
    /// <summary>Who made the GPU inference would use.</summary>
    public readonly GpuManufacturer Manufacturer;

    /// <summary>The GPU's model name, e.g. "NVIDIA GeForce RTX 4070", or "Unknown".</summary>
    public readonly string GpuModelName;

    /// <summary>Whether the GPU runs CUDA 12 or CUDA 13.</summary>
    public readonly bool SupportsCuda;

    /// <summary>Maxwell (5.0) or newer, and a driver that runs CUDA 12 (525+).</summary>
    public readonly bool SupportsCuda12;

    /// <summary>Turing (7.5) or newer, and a driver that runs CUDA 13 (580+).</summary>
    public readonly bool SupportsCuda13;

    /// <summary>The CUDA compute capability's major part; 0 unless this is an NVIDIA GPU with its driver loaded.</summary>
    public readonly int ComputeCapabilityMajor;

    /// <summary>The CUDA compute capability's minor part.</summary>
    public readonly int ComputeCapabilityMinor;

    /// <summary>The newest CUDA the driver runs, as major * 1000 + minor * 10; 0 when unknown.</summary>
    public readonly int DriverCudaVersion;

    /// <summary>Whether DirectML can run here: Windows 10 1903 or later with a Direct3D 12 GPU.</summary>
    public readonly bool SupportsDirectMl;

    /// <summary>
    /// Whether OpenVINO's GPU plugin could run here. The mod's pinned OpenVINO ships only the CPU
    /// plugin, so this is informational.
    /// </summary>
    public readonly bool SupportsOpenVino;

    /// <summary>Whether NVIDIA's TensorRT RTX plugin can run here; see <see cref="DoesSupportTensorrtRtx"/>.</summary>
    public readonly bool SupportsTensorrtRtx;

    /// <summary>Whether Core ML can run here: any Mac on macOS 10.15 or later.</summary>
    public readonly bool SupportsCoreMl;

    /// <summary>The first AMD GPU ROCm can see, as its LLVM target ("gfx1100"); null when there is none.</summary>
    public readonly string AmdGfxTarget;

    /// <summary>Creates a result; see the fields.</summary>
    /// <param name="manufacturer">Who made the GPU.</param>
    /// <param name="gpuModelName">Its model name.</param>
    /// <param name="supportsCuda12">Whether it runs CUDA 12.</param>
    /// <param name="supportsCuda13">Whether it runs CUDA 13.</param>
    /// <param name="computeCapabilityMajor">Compute capability, major part.</param>
    /// <param name="computeCapabilityMinor">Compute capability, minor part.</param>
    /// <param name="driverCudaVersion">The newest CUDA the driver runs.</param>
    /// <param name="supportsDirectMl">Whether DirectML can run.</param>
    /// <param name="supportsOpenVino">Whether OpenVINO's GPU plugin could run.</param>
    /// <param name="supportsTensorrtRtx">Whether TensorRT RTX can run.</param>
    /// <param name="supportsCoreMl">Whether Core ML can run.</param>
    /// <param name="amdGfxTarget">ROCm's first GPU target, or null.</param>
    private GpuInfo(GpuManufacturer manufacturer, string gpuModelName, bool supportsCuda12, bool supportsCuda13,
        int computeCapabilityMajor, int computeCapabilityMinor, int driverCudaVersion, bool supportsDirectMl,
        bool supportsOpenVino, bool supportsTensorrtRtx, bool supportsCoreMl, string amdGfxTarget)
    {
        Manufacturer = manufacturer;
        GpuModelName = gpuModelName;
        SupportsCuda = supportsCuda12 || supportsCuda13;
        SupportsCuda12 = supportsCuda12;
        SupportsCuda13 = supportsCuda13;
        ComputeCapabilityMajor = computeCapabilityMajor;
        ComputeCapabilityMinor = computeCapabilityMinor;
        DriverCudaVersion = driverCudaVersion;
        SupportsDirectMl = supportsDirectMl;
        SupportsOpenVino = supportsOpenVino;
        SupportsTensorrtRtx = supportsTensorrtRtx;
        SupportsCoreMl = supportsCoreMl;
        AmdGfxTarget = amdGfxTarget;
    }

    /// <summary>Whether this GPU runs the given CUDA major version, e.g. the one <c>DetectCudaMajorVersion</c> picked.</summary>
    public bool SupportsCudaMajor(int major) => major switch
    {
        12 => SupportsCuda12,
        13 => SupportsCuda13,
        _ => false
    };

    /// <summary>This machine's result, detected once.</summary>
    private static readonly Lazy<GpuInfo> _current = new(Detect);

    /// <summary>This machine's GPU, detected on first use. Detection loads driver libraries and may start a process, so it runs once.</summary>
    public static GpuInfo Current => _current.Value;

    /// <summary>Detects the GPU now, without caching; use <see cref="Current"/>.</summary>
    /// <returns>The result; everything unsupported if detection itself failed.</returns>
    public static GpuInfo Detect()
    {
        try
        {
            return DetectCore();
        }
        catch
        {
            return new GpuInfo(GpuManufacturer.Unknown, "Unknown", false, false, 0, 0, 0,
                false, false, false, false, null);
        }
    }

    /// <summary>Finds the primary adapter, asks NVML, and checks each runtime.</summary>
    /// <returns>The result.</returns>
    private static GpuInfo DetectCore()
    {
        Adapter adapter = FindPrimaryAdapter();

        // NVML runs on Windows and Linux whenever the proprietary driver is loaded. It is asked even when
        // the PCI scan found no NVIDIA card: WSL and containers expose the GPU without a PCI device.
        NvidiaDevice nvidia = OperatingSystem.IsWindows() || OperatingSystem.IsLinux() ? Nvml.Query() : null;
        GpuManufacturer manufacturer = nvidia != null ? GpuManufacturer.Nvidia : adapter.Manufacturer;

        string gpuModelName = GetGpuModelName(adapter, nvidia);
        bool supportsCuda12 = Probe(() => DoesSupportCuda(manufacturer, nvidia, 12));
        bool supportsCuda13 = Probe(() => DoesSupportCuda(manufacturer, nvidia, 13));
        bool supportsDirectMl = Probe(DoesSupportDirectMl);
        bool supportsOpenVino = Probe(() => DoesSupportOpenVino(manufacturer, adapter));
        bool supportsTensorrtRtx = Probe(() => DoesSupportTensorrtRtx(manufacturer, nvidia));
        bool supportsCoreMl = Probe(DoesSupportCoreMl);
        string amdGfxTarget = OperatingSystem.IsLinux() ? KfdGfxTarget() : null;

        return new GpuInfo(manufacturer, gpuModelName, supportsCuda12, supportsCuda13,
            (nvidia?.ComputeCapability ?? 0) / 10, (nvidia?.ComputeCapability ?? 0) % 10, nvidia?.CudaDriverVersion ?? 0,
            supportsDirectMl, supportsOpenVino, supportsTensorrtRtx, supportsCoreMl, amdGfxTarget);
    }

    /// <summary>Runs one capability check, treating any exception as unsupported.</summary>
    /// <param name="check">The check.</param>
    /// <returns>Its answer, or false.</returns>
    private static bool Probe(Func<bool> check)
    {
        try { return check(); }
        catch { return false; }
    }


    /// <summary>One display adapter the OS reported.</summary>
    /// <param name="PciDeviceId">0 when the OS did not say.</param>
    /// <param name="TieBreak">Orders adapters from the same vendor; higher is more likely the discrete one.</param>
    private readonly record struct Adapter(GpuManufacturer Manufacturer, string Name, int PciDeviceId, long TieBreak);

    /// <summary>What NVML reports about the GPU CUDA would use.</summary>
    private sealed class NvidiaDevice
    {
        /// <summary>The marketing name.</summary>
        public string Name;

        /// <summary>Major * 10 + minor, so 8.6 is 86.</summary>
        public int ComputeCapability;

        /// <summary>The newest CUDA the driver runs, major * 1000 + minor * 10, so 13.0 is 13000.</summary>
        public int CudaDriverVersion;
    }

    /// <summary>
    /// A laptop or desktop with an iGPU reports two adapters. The one inference should use is the
    /// discrete card, which is NVIDIA or AMD whenever it is paired with an Intel or AMD iGPU.
    /// </summary>
    private static Adapter FindPrimaryAdapter()
    {
        List<Adapter> adapters;
        try
        {
            if (OperatingSystem.IsWindows()) adapters = Dxgi.Adapters();
            else if (OperatingSystem.IsLinux()) adapters = LinuxAdapters();
            else if (OperatingSystem.IsMacOS()) adapters = MacAdapters();
            else adapters = new List<Adapter>();
        }
        catch { adapters = new List<Adapter>(); /* treat probe failures as "no GPU" */ }

        var best = new Adapter(GpuManufacturer.Unknown, null, 0, -1);
        foreach (Adapter adapter in adapters)
        {
            int rank = VendorRank(adapter.Manufacturer), bestRank = VendorRank(best.Manufacturer);
            if (rank > bestRank || rank == bestRank && adapter.TieBreak > best.TieBreak) best = adapter;
        }

        // Apple silicon has no GPU but its own.
        if (best.Manufacturer == GpuManufacturer.Unknown && OperatingSystem.IsMacOS() &&
            RuntimeInformation.OSArchitecture == Architecture.Arm64)
            best = best with { Manufacturer = GpuManufacturer.Apple };
        return best;
    }

    /// <summary>How likely a vendor's adapter is the one to use: NVIDIA, then AMD, Intel, Apple.</summary>
    /// <param name="manufacturer">The vendor.</param>
    /// <returns>Higher is preferred.</returns>
    private static int VendorRank(GpuManufacturer manufacturer) => manufacturer switch
    {
        GpuManufacturer.Nvidia => 4,
        GpuManufacturer.Amd => 3,
        GpuManufacturer.Intel => 2,
        GpuManufacturer.Apple => 1,
        _ => 0
    };

    /// <summary>The manufacturer for a PCI vendor id.</summary>
    /// <param name="pciVendorId">The vendor id.</param>
    /// <returns>The manufacturer, or Unknown.</returns>
    private static GpuManufacturer ManufacturerFor(int pciVendorId) => pciVendorId switch
    {
        0x10DE => GpuManufacturer.Nvidia,
        0x1002 or 0x1022 => GpuManufacturer.Amd,
        0x8086 => GpuManufacturer.Intel,
        0x106B => GpuManufacturer.Apple,
        _ => GpuManufacturer.Unknown
    };

    /// <summary>The GPU's name: NVML's marketing name when there is one, else the adapter's.</summary>
    /// <param name="adapter">The adapter.</param>
    /// <param name="nvidia">What NVML said, or null.</param>
    /// <returns>The name, or "Unknown".</returns>
    private static string GetGpuModelName(Adapter adapter, NvidiaDevice nvidia)
    {
        // NVML's name is the marketing name on every OS; pci.ids on Linux gives the die name first.
        if (!string.IsNullOrEmpty(nvidia?.Name)) return nvidia.Name;
        return string.IsNullOrEmpty(adapter.Name) ? "Unknown" : adapter.Name;
    }

    /// <summary>The first of the named exports the library has, or null.</summary>
    private static T Export<T>(IntPtr library, params string[] names) where T : Delegate
    {
        foreach (string name in names)
            if (NativeLibrary.TryGetExport(library, name, out IntPtr address))
                return Marshal.GetDelegateForFunctionPointer<T>(address);
        return null;
    }
}
