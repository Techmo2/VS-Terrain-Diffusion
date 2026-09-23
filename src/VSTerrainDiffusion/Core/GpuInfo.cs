using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace VSTerrainDiffusion.Core;

public enum GpuManufacturer
{
    Amd,
    Nvidia,
    Intel,
    Apple,
    Unknown
}

/*
 * Platform dependent gpu information.
 * This struct tells us what we can and cannot theoretically accomplish with this gpu and operating system.
 * Detection never throws: anything it cannot establish comes back as unsupported.
 */
public readonly struct GpuInfo
{
    public readonly GpuManufacturer Manufacturer;
    public readonly String GpuModelName; // What is this gpu's model name?
    public readonly bool SupportsCuda; // Does this gpu support cuda12 or cuda 13?
    public readonly bool SupportsCuda12; // Maxwell (5.0) or newer, and a driver that runs CUDA 12 (525+)
    public readonly bool SupportsCuda13; // Turing (7.5) or newer, and a driver that runs CUDA 13 (580+)
    public readonly int ComputeCapabilityMajor; // 0 when this is not an NVIDIA GPU with its driver loaded
    public readonly int ComputeCapabilityMinor;
    public readonly int DriverCudaVersion; // The newest CUDA the driver runs, as major * 1000 + minor * 10; 0 when unknown
    public readonly bool SupportsDirectMl; // Does this gpu support DirectML?
    public readonly bool SupportsOpenVino; // The GPU plugin could run here. The mod's pinned OpenVINO ships only the CPU plugin.
    public readonly bool SupportsTensorrtRtx;
    public readonly bool SupportsCoreMl;

    GpuInfo(GpuManufacturer manufacturer, String gpuModelName, bool supportsCuda12, bool supportsCuda13,
        int computeCapabilityMajor, int computeCapabilityMinor, int driverCudaVersion, bool supportsDirectMl,
        bool supportsOpenVino, bool supportsTensorrtRtx, bool supportsCoreMl)
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
    }

    /// <summary>Whether this GPU runs the given CUDA major version, e.g. the one <c>DetectCudaMajorVersion</c> picked.</summary>
    public bool SupportsCudaMajor(int major) => major switch
    {
        12 => SupportsCuda12,
        13 => SupportsCuda13,
        _ => false
    };

    private static readonly Lazy<GpuInfo> _current = new(Detect);

    /// <summary>This machine's GPU, detected on first use. Detection loads driver libraries and may start a process, so it runs once.</summary>
    public static GpuInfo Current => _current.Value;

    public static GpuInfo Detect()
    {
        try
        {
            return DetectCore();
        }
        catch
        {
            return new GpuInfo(GpuManufacturer.Unknown, "Unknown", false, false, 0, 0, 0,
                false, false, false, false);
        }
    }

    private static GpuInfo DetectCore()
    {
        Adapter adapter = FindPrimaryAdapter();

        // NVML runs on Windows and Linux whenever the proprietary driver is loaded. It is asked even when
        // the PCI scan found no NVIDIA card: WSL and containers expose the GPU without a PCI device.
        NvidiaDevice nvidia = OperatingSystem.IsWindows() || OperatingSystem.IsLinux() ? Nvml.Query() : null;
        GpuManufacturer manufacturer = nvidia != null ? GpuManufacturer.Nvidia : adapter.Manufacturer;

        String gpuModelName = GetGpuModelName(adapter, nvidia);
        bool supportsCuda12 = Probe(() => DoesSupportCuda(manufacturer, nvidia, 12));
        bool supportsCuda13 = Probe(() => DoesSupportCuda(manufacturer, nvidia, 13));
        bool supportsDirectMl = Probe(DoesSupportDirectMl);
        bool supportsOpenVino = Probe(() => DoesSupportOpenVino(manufacturer, adapter));
        bool supportsTensorrtRtx = Probe(() => DoesSupportTensorrtRtx(manufacturer, nvidia));
        bool supportsCoreMl = Probe(DoesSupportCoreMl);

        return new GpuInfo(manufacturer, gpuModelName, supportsCuda12, supportsCuda13,
            (nvidia?.ComputeCapability ?? 0) / 10, (nvidia?.ComputeCapability ?? 0) % 10, nvidia?.CudaDriverVersion ?? 0,
            supportsDirectMl, supportsOpenVino, supportsTensorrtRtx, supportsCoreMl);
    }

    private static bool Probe(Func<bool> check)
    {
        try { return check(); }
        catch { return false; }
    }

    // GPU Detection

    /// <summary>One display adapter the OS reported.</summary>
    /// <param name="PciDeviceId">0 when the OS did not say.</param>
    /// <param name="TieBreak">Orders adapters from the same vendor; higher is more likely the discrete one.</param>
    private readonly record struct Adapter(GpuManufacturer Manufacturer, string Name, int PciDeviceId, long TieBreak);

    private sealed class NvidiaDevice
    {
        public string Name;
        public int ComputeCapability; // major * 10 + minor, so 8.6 is 86
        public int CudaDriverVersion; // major * 1000 + minor * 10, so 13.0 is 13000
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

    private static int VendorRank(GpuManufacturer manufacturer) => manufacturer switch
    {
        GpuManufacturer.Nvidia => 4,
        GpuManufacturer.Amd => 3,
        GpuManufacturer.Intel => 2,
        GpuManufacturer.Apple => 1,
        _ => 0
    };

    private static GpuManufacturer ManufacturerFor(int pciVendorId) => pciVendorId switch
    {
        0x10DE => GpuManufacturer.Nvidia,
        0x1002 or 0x1022 => GpuManufacturer.Amd,
        0x8086 => GpuManufacturer.Intel,
        0x106B => GpuManufacturer.Apple,
        _ => GpuManufacturer.Unknown
    };

    private static String GetGpuModelName(Adapter adapter, NvidiaDevice nvidia)
    {
        // NVML's name is the marketing name on every OS; pci.ids on Linux gives the die name first.
        if (!string.IsNullOrEmpty(nvidia?.Name)) return nvidia.Name;
        return string.IsNullOrEmpty(adapter.Name) ? "Unknown" : adapter.Name;
    }

    private static bool DoesSupportCuda(GpuManufacturer manufacturer, NvidiaDevice nvidia, int cudaMajor)
    {
        // Only nvidia GPUs support cuda
        // In the future, we can add support for ROCM
        // No NVML means no proprietary driver (nouveau, or none), and CUDA needs that driver.
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

    private static bool DoesSupportDirectMl()
    {
        // DirectML is only supported on Windows, from Windows 10 1903
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 18362))
            return false;

        // DirectML runs on any vendor's GPU with a Direct3D 12 driver at feature level 11_0.
        return Dxgi.SupportsDirect3D12();
    }

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

    // TensorRT RTX's supported GPUs, which are also the targets of its default engine: Ampere GA10x (8.6),
    // Ada (8.9) and consumer Blackwell (12.0, 12.1). Turing (7.5) is supported but left out of the
    // default engine, and datacenter parts (8.0, 9.0, 10.x) are not supported at all.
    private static readonly int[] TensorRtRtxComputeCapabilities = { 86, 89, 120, 121 };

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

    private static bool DoesSupportCoreMl()
    {
        // CoreML is only supported on Apple Systems. It runs on every Mac: the GPU and Neural Engine
        // on Apple silicon, the GPU on an Intel Mac. ONNX Runtime's provider needs macOS 10.15.
        return OperatingSystem.IsMacOSVersionAtLeast(10, 15);
    }

    // Linux

    private static List<Adapter> LinuxAdapters()
    {
        var adapters = new List<Adapter>();
        const string root = "/sys/bus/pci/devices";
        if (!Directory.Exists(root)) return adapters;

        foreach (string device in Directory.EnumerateFileSystemEntries(root))
        {
            try
            {
                // PCI class 0x03xxxx is a display controller: VGA, 3D or other.
                if (!File.ReadAllText(Path.Combine(device, "class")).Trim().StartsWith("0x03", StringComparison.Ordinal))
                    continue;
                int vendor = ParseHex(File.ReadAllText(Path.Combine(device, "vendor")));
                int deviceId = ParseHex(File.ReadAllText(Path.Combine(device, "device")));

                // amdgpu fills product_name from the VBIOS on some cards; pci.ids covers the rest.
                string productName = Path.Combine(device, "product_name");
                string name = File.Exists(productName) ? File.ReadAllText(productName).Trim() : null;
                if (string.IsNullOrEmpty(name)) name = PciIdsName(vendor, deviceId);
                name ??= $"{ManufacturerFor(vendor)} device 0x{deviceId:x4}";

                // Intel's iGPU is always 0000:00:02.0, on the root bus; a discrete card sits behind a bridge.
                string address = Path.GetFileName(device);
                long tieBreak = address.Length >= 7 && address.Substring(5, 2) != "00" ? 1 : 0;

                adapters.Add(new Adapter(ManufacturerFor(vendor), name, deviceId, tieBreak));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (FormatException) { }
            catch (OverflowException) { }
        }
        return adapters;
    }

    private static int ParseHex(string text)
    {
        text = text.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) text = text.Substring(2);
        return int.Parse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    }

    /// <summary>The device's name from the system's copy of the PCI ID database, if it has one.</summary>
    private static string PciIdsName(int vendor, int device)
    {
        foreach (string path in new[] { "/usr/share/hwdata/pci.ids", "/usr/share/misc/pci.ids", "/usr/share/pci.ids" })
        {
            if (!File.Exists(path)) continue;

            // Vendors are "10de  NVIDIA Corporation"; their devices follow as "\t2684  AD102 [GeForce RTX 4090]".
            string vendorPrefix = vendor.ToString("x4") + " ";
            string devicePrefix = "\t" + device.ToString("x4") + " ";
            bool inVendor = false;
            foreach (string line in File.ReadLines(path))
            {
                if (line.Length == 0 || line[0] == '#') continue;
                if (line[0] != '\t')
                {
                    if (inVendor) return null;
                    inVendor = line.StartsWith(vendorPrefix, StringComparison.Ordinal);
                }
                else if (inVendor && line.StartsWith(devicePrefix, StringComparison.Ordinal))
                {
                    return line.Substring(devicePrefix.Length).Trim();
                }
            }
            return null;
        }
        return null;
    }

    // macOS

    private static List<Adapter> MacAdapters()
    {
        var adapters = new List<Adapter>();
        string output = RunProcess("/usr/sbin/system_profiler", "SPDisplaysDataType");
        if (output == null) return adapters;

        // One block per GPU:
        //   Chipset Model: AMD Radeon Pro 5500M
        //   Bus: PCIe
        //   Vendor: AMD (0x1002)
        //   Device ID: 0x7340
        string name = null;
        var manufacturer = GpuManufacturer.Unknown;
        int deviceId = 0;
        bool builtIn = false;
        foreach (string rawLine in output.Split('\n'))
        {
            string line = rawLine.Trim();
            int colon = line.IndexOf(':');
            if (colon < 0) continue;
            string key = line.Substring(0, colon), value = line.Substring(colon + 1).Trim();

            if (key == "Chipset Model")
            {
                if (name != null) adapters.Add(new Adapter(manufacturer, name, deviceId, builtIn ? 0 : 1));
                name = value;
                manufacturer = GpuManufacturer.Unknown;
                deviceId = 0;
                builtIn = false;
            }
            else if (key == "Bus") builtIn = value == "Built-In";
            else if (key == "Vendor") manufacturer = MacVendor(value);
            else if (key == "Device ID") deviceId = TryParseHexToken(value);
        }
        if (name != null) adapters.Add(new Adapter(manufacturer, name, deviceId, builtIn ? 0 : 1));
        return adapters;
    }

    private static GpuManufacturer MacVendor(string vendor)
    {
        // "Apple (0x106b)", "Intel", and on some releases "sppci_vendor_Apple".
        int vendorId = TryParseHexToken(vendor);
        if (vendorId != 0) return ManufacturerFor(vendorId);

        string lower = vendor.ToLowerInvariant();
        if (lower.Contains("nvidia")) return GpuManufacturer.Nvidia;
        if (lower.Contains("amd") || lower.Contains("ati")) return GpuManufacturer.Amd;
        if (lower.Contains("intel")) return GpuManufacturer.Intel;
        if (lower.Contains("apple")) return GpuManufacturer.Apple;
        return GpuManufacturer.Unknown;
    }

    /// <summary>The four hex digits after the first "0x" in <paramref name="text"/>, or 0.</summary>
    private static int TryParseHexToken(string text)
    {
        int start = text.IndexOf("0x", StringComparison.OrdinalIgnoreCase);
        if (start < 0 || text.Length < start + 6) return 0;
        return int.TryParse(text.Substring(start + 2, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int value)
            ? value
            : 0;
    }

    private static string RunProcess(string fileName, string arguments)
    {
        try
        {
            using Process process = Process.Start(new ProcessStartInfo(fileName, arguments)
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (process == null) return null;
            var output = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(10_000))
            {
                process.Kill(entireProcessTree: true);
                return null;
            }
            return output.Result;
        }
        catch { return null; }
    }

    // Windows

    [SupportedOSPlatform("windows")]
    private static class Dxgi
    {
        private static readonly Guid IidDxgiFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");
        private static readonly Guid IidD3D12Device = new("189819f1-1db6-4b57-be54-1821339b85f7");
        private const int D3DFeatureLevel11_0 = 0xB000;
        private const uint DxgiAdapterFlagSoftware = 2;
        private const uint MaxAdapters = 64;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct AdapterDesc1
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string Description;
            public uint VendorId;
            public uint DeviceId;
            public uint SubSysId;
            public uint Revision;
            public nuint DedicatedVideoMemory;
            public nuint DedicatedSystemMemory;
            public nuint SharedSystemMemory;
            public uint AdapterLuidLow;
            public int AdapterLuidHigh;
            public uint Flags;
        }

        private delegate int EnumAdapters1Fn(IntPtr factory, uint index, out IntPtr adapter);
        private delegate int GetDesc1Fn(IntPtr adapter, out AdapterDesc1 desc);

        [DllImport("dxgi.dll")]
        private static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr factory);

        [DllImport("d3d12.dll")]
        private static extern int D3D12CreateDevice(IntPtr adapter, int minimumFeatureLevel, ref Guid riid, IntPtr device);

        /// <summary>Adapters that are present now; the registry keeps entries for cards long removed.</summary>
        public static List<Adapter> Adapters()
        {
            var adapters = new List<Adapter>();
            Guid iid = IidDxgiFactory1;
            if (CreateDXGIFactory1(ref iid, out IntPtr factory) < 0 || factory == IntPtr.Zero) return adapters;
            try
            {
                // IDXGIFactory1::EnumAdapters1 and IDXGIAdapter1::GetDesc1 by vtable slot.
                var enumAdapters = ComMethod<EnumAdapters1Fn>(factory, 12);
                for (uint i = 0; i < MaxAdapters && enumAdapters(factory, i, out IntPtr adapter) >= 0; i++)
                {
                    if (adapter == IntPtr.Zero) continue;
                    try
                    {
                        if (ComMethod<GetDesc1Fn>(adapter, 10)(adapter, out AdapterDesc1 desc) < 0) continue;
                        // The Basic Render Driver is a CPU rasteriser.
                        if ((desc.Flags & DxgiAdapterFlagSoftware) != 0) continue;
                        adapters.Add(new Adapter(ManufacturerFor((int)desc.VendorId), desc.Description?.Trim(),
                            (int)desc.DeviceId, (long)desc.DedicatedVideoMemory));
                    }
                    finally { Marshal.Release(adapter); }
                }
            }
            finally { Marshal.Release(factory); }
            return adapters;
        }

        /// <summary>Asks for a device without anywhere to put it, which only tests that one could be made.</summary>
        public static bool SupportsDirect3D12()
        {
            try
            {
                Guid iid = IidD3D12Device;
                return D3D12CreateDevice(IntPtr.Zero, D3DFeatureLevel11_0, ref iid, IntPtr.Zero) >= 0;
            }
            catch (DllNotFoundException) { return false; }
            catch (EntryPointNotFoundException) { return false; }
        }

        private static T ComMethod<T>(IntPtr self, int slot) where T : Delegate =>
            Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(self), slot * IntPtr.Size));
    }

    // NVIDIA

    /// <summary>
    /// The NVIDIA Management Library, shipped with the display driver on Windows and Linux. Unlike
    /// the CUDA driver API it answers without creating a CUDA context.
    /// </summary>
    private static class Nvml
    {
        private const int Success = 0;
        private const int NameBufferSize = 96; // NVML_DEVICE_NAME_V2_BUFFER_SIZE
        private const uint MaxDevices = 64;

        private delegate int InitFn();
        private delegate int ShutdownFn();
        private delegate int GetCountFn(out uint count);
        private delegate int GetHandleByIndexFn(uint index, out IntPtr device);
        private delegate int GetNameFn(IntPtr device, [Out] byte[] name, uint length);
        private delegate int GetComputeCapabilityFn(IntPtr device, out int major, out int minor);
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

    // Intel

    /// <summary>The OpenCL ICD loader, which lists every vendor runtime installed.</summary>
    private static class OpenCl
    {
        private const int Success = 0;
        private const ulong DeviceTypeGpu = 1 << 2; // CL_DEVICE_TYPE_GPU
        private const uint PlatformVendor = 0x0903; // CL_PLATFORM_VENDOR
        private const uint MaxPlatforms = 64;

        private delegate int GetPlatformIdsFn(uint entries, [Out] IntPtr[] platforms, out uint count);
        private delegate int GetPlatformInfoFn(IntPtr platform, uint param, nuint size, [Out] byte[] value, out nuint sizeReturned);
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

    /// <summary>The first of the named exports the library has, or null.</summary>
    private static T Export<T>(IntPtr library, params string[] names) where T : Delegate
    {
        foreach (string name in names)
            if (NativeLibrary.TryGetExport(library, name, out IntPtr address))
                return Marshal.GetDelegateForFunctionPointer<T>(address);
        return null;
    }
}
