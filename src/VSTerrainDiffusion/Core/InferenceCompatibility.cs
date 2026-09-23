using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using Vintagestory.API.Common;
using VSTerrainDiffusion.Native;

namespace VSTerrainDiffusion.Core;

/// <summary>
/// Which inference devices and model precisions this machine can run, worked out once per process
/// from the operating system, the GPU and its driver.
///
/// It is the single answer to "can this run here": the settings screen offers only what it allows
/// (see <see cref="ConfigLibOptionsFilter"/>), the config is refused at startup when it names
/// anything else, and the "auto" device picks from it. Nothing in the mod changes the device or a
/// precision on the player's behalf - a value that cannot run stops the game with the reason, and
/// only the player edits it.
/// </summary>
public sealed class InferenceCompatibility
{
    /// <summary>Every device the config understands, in the order the settings screen lists them.</summary>
    public static readonly string[] AllDevices =
        { "auto", "cpu", "openvino", "cuda", "tensorrt-rtx", "directml", "coreml" };

    public static readonly string[] AllCoarsePrecisions = { "fp32", "fp16" };
    public static readonly string[] AllBasePrecisions = { "fp32", "fp16" };
    public static readonly string[] AllDecoderPrecisions = { "fp32", "fp16", "int8" };

    /// <summary>The providers that run on a GPU, and so the only ones FP16 models are worth running on.</summary>
    private static readonly string[] GpuDevices = { "cuda", "tensorrt-rtx", "directml", "coreml" };

    private static readonly Lazy<InferenceCompatibility> _current = new(() => new InferenceCompatibility(GpuInfo.Current));

    /// <summary>This machine, checked on first use.</summary>
    public static InferenceCompatibility Current => _current.Value;

    public GpuInfo Gpu { get; }

    /// <summary>Why each device that cannot run here cannot; a device with no entry is compatible.</summary>
    private readonly Dictionary<string, string> _deviceProblems = new(StringComparer.Ordinal);

    private readonly string _halfPrecisionProblem;

    private int _logged;

    private InferenceCompatibility(GpuInfo gpu)
    {
        Gpu = gpu;

        foreach (string device in AllDevices)
        {
            string problem = DeviceProblem(device, gpu);
            if (problem != null) _deviceProblems[device] = problem;
        }

        if (!Array.Exists(GpuDevices, IsDeviceCompatible))
            _halfPrecisionProblem = "FP16 models need a GPU provider (cuda, tensorrt-rtx, directml or coreml), " +
                                    "and none can run on this machine";
    }

    public bool IsDeviceCompatible(string device) =>
        device switch
        {
            // Not offered in the settings screen, but older configs carry it: the automatic choice,
            // with a warning if that choice is not a GPU.
            "gpu" => Array.Exists(GpuDevices, IsDeviceCompatible),
            _ => Array.IndexOf(AllDevices, device) >= 0 && !_deviceProblems.ContainsKey(device)
        };

    /// <summary>The devices this machine can run, in settings-screen order.</summary>
    public List<string> CompatibleDevices() => new(Array.FindAll(AllDevices, IsDeviceCompatible));

    /// <summary>The precisions this machine can run for one model, in settings-screen order.</summary>
    public List<string> CompatiblePrecisions(IEnumerable<string> all)
    {
        var compatible = new List<string>();
        foreach (string precision in all)
            if (PrecisionProblem(precision) == null) compatible.Add(precision);
        return compatible;
    }

    /// <summary>
    /// What "auto" runs on here: CoreML on macOS, DirectML on Windows and CUDA on Linux when this
    /// machine can run them, and the CPU otherwise. OpenVINO and TensorRT RTX are only ever chosen
    /// by name.
    /// </summary>
    public string AutomaticDevice()
    {
        if (OperatingSystem.IsMacOS() && IsDeviceCompatible("coreml")) return "coreml";
        if (OperatingSystem.IsWindows() && IsDeviceCompatible("directml")) return "directml";
        if (OperatingSystem.IsLinux() && IsDeviceCompatible("cuda")) return "cuda";
        return "cpu";
    }

    /// <summary>
    /// Stops the game if <paramref name="config"/> names a device or precision this machine cannot
    /// run. Called as the mod loads, before anything is downloaded or any world is touched.
    /// </summary>
    public void Require(DiffusionConfig config, ILogger logger)
    {
        RequireDevice(config.InferenceDevice, logger);
        RequirePrecision(nameof(DiffusionConfig.CoarsePrecision), config.CoarsePrecision, AllCoarsePrecisions, logger);
        RequirePrecision(nameof(DiffusionConfig.BasePrecision), config.BasePrecision, AllBasePrecisions, logger);
        RequirePrecision(nameof(DiffusionConfig.DecoderPrecision), config.DecoderPrecision, AllDecoderPrecisions, logger);
    }

    public void RequireDevice(string device, ILogger logger)
    {
        if (IsDeviceCompatible(device)) return;

        string reason = device == "gpu"
            ? "no GPU provider can run on this machine"
            : _deviceProblems.TryGetValue(device ?? "", out string problem) ? problem : "it is not a known inference device";
        throw DiffusionFailure.Fatal(logger,
            $"{nameof(DiffusionConfig.InferenceDevice)} \"{device}\" in {DiffusionPaths.ModId}.json cannot be used: " +
            $"{reason}. This machine can use: {string.Join(", ", CompatibleDevices())}.");
    }

    public void RequirePrecision(string setting, string precision, string[] all, ILogger logger)
    {
        string reason = Array.IndexOf(all, precision) < 0
            ? $"it is not one of {string.Join(", ", all)}"
            : PrecisionProblem(precision);
        if (reason == null) return;

        throw DiffusionFailure.Fatal(logger,
            $"{setting} \"{precision}\" in {DiffusionPaths.ModId}.json cannot be used: {reason}. " +
            $"This machine can use: {string.Join(", ", CompatiblePrecisions(all))}.");
    }

    /// <summary>Writes what was found and what it allows, so a refused config can be explained from the log.</summary>
    public void Log(ILogger logger)
    {
        // The server runs StartPre more than once, and the hardware does not change in between.
        if (System.Threading.Interlocked.Exchange(ref _logged, 1) != 0) return;

        string capability = Gpu.ComputeCapabilityMajor > 0
            ? $", compute capability {Gpu.ComputeCapabilityMajor}.{Gpu.ComputeCapabilityMinor}" +
              $", driver CUDA {Gpu.DriverCudaVersion / 1000}.{Gpu.DriverCudaVersion % 1000 / 10}"
            : "";
        logger.Notification("[{0}] GPU: {1} ({2}{3}) on {4} {5}", DiffusionPaths.ModId,
            Gpu.GpuModelName, Gpu.Manufacturer, capability, RuntimeInformation.OSDescription,
            RuntimeInformation.OSArchitecture);

        logger.Notification("[{0}] Compatible inference devices: {1} (auto = {2}); precisions: coarse {3}, base {4}, decoder {5}",
            DiffusionPaths.ModId, string.Join(", ", CompatibleDevices()), AutomaticDevice(),
            string.Join("/", CompatiblePrecisions(AllCoarsePrecisions)),
            string.Join("/", CompatiblePrecisions(AllBasePrecisions)),
            string.Join("/", CompatiblePrecisions(AllDecoderPrecisions)));

        foreach (string device in AllDevices)
            if (_deviceProblems.TryGetValue(device, out string problem))
                logger.Debug("[{0}] {1} unavailable: {2}", DiffusionPaths.ModId, device, problem);
    }

    private string PrecisionProblem(string precision) => precision switch
    {
        "fp32" => null,
        "fp16" => _halfPrecisionProblem,
        // The INT8 decoder runs on ONNX Runtime's CPU provider, which every supported machine has.
        "int8" => _deviceProblems.TryGetValue("cpu", out string cpu) ? cpu : null,
        _ => "it is not a known precision"
    };

    /// <summary>Why <paramref name="device"/> cannot run on this machine, or null when it can.</summary>
    private static string DeviceProblem(string device, GpuInfo gpu)
    {
        bool windows = OperatingSystem.IsWindows(), linux = OperatingSystem.IsLinux(), macos = OperatingSystem.IsMacOS();
        Architecture arch = RuntimeInformation.OSArchitecture;
        bool x64 = arch == Architecture.X64;
        string gpuName = $"{gpu.GpuModelName} ({gpu.Manufacturer})";

        // ONNX Runtime publishes win, linux and osx builds for x64 and arm64, and nothing else.
        if (!(windows || linux || macos) || arch is not (Architecture.X64 or Architecture.Arm64))
            return $"ONNX Runtime has no build for {RuntimeInformation.OSDescription} on {arch}";
        // Its macOS build is Apple silicon only; there is no Intel Mac build of this version.
        if (macos && arch != Architecture.Arm64)
            return $"ONNX Runtime {OnnxRuntimeBootstrap.OnnxRuntimeVersion} has no build for Intel Macs, only Apple silicon";

        switch (device)
        {
            case "auto":
            case "cpu":
                return null;

            case "openvino":
                // The mod's pinned OpenVINO is its Linux x64 CPU plugin, which needs SSE4.2.
                if (!linux || !x64) return "the OpenVINO runtime this mod uses is for 64-bit Linux only";
                if (!Sse42.IsSupported) return "OpenVINO's CPU plugin needs a processor with SSE4.2";
                return null;

            case "cuda":
            {
                if (!(windows || linux) || !x64) return "the CUDA provider needs 64-bit Windows or Linux";
                if (gpu.Manufacturer != GpuManufacturer.Nvidia || gpu.ComputeCapabilityMajor == 0)
                    return $"it needs an NVIDIA GPU with NVIDIA's driver loaded; found {gpuName}";

                // The ONNX Runtime build has to match the CUDA libraries it will load: the system's
                // own, or on a Windows machine without any, the CUDA 13 set the mod downloads.
                int major = OnnxRuntimeBootstrap.DetectCudaMajorVersion();
                if (!gpu.SupportsCudaMajor(major))
                    return $"this machine's CUDA libraries are CUDA {major}, which the {gpu.GpuModelName} " +
                           $"(compute capability {gpu.ComputeCapabilityMajor}.{gpu.ComputeCapabilityMinor}) " +
                           $"with a driver for CUDA {gpu.DriverCudaVersion / 1000}.{gpu.DriverCudaVersion % 1000 / 10} " +
                           $"cannot run (CUDA 12 needs compute capability 5.0 and driver 525+, CUDA 13 needs 7.5 and 580+)";

                if (linux)
                {
                    List<string> missing = MissingLinuxCudaLibraries(major);
                    if (missing.Count > 0)
                        return $"the CUDA {major} toolkit and cuDNN 9 are not installed (missing {string.Join(", ", missing)}); " +
                               "on Linux the CUDA provider uses the system's CUDA install";
                }
                return null;
            }

            case "tensorrt-rtx":
                if (!(windows || linux) || !x64) return "TensorRT RTX needs 64-bit Windows or Linux";
                if (!gpu.SupportsTensorrtRtx)
                    return "TensorRT RTX needs a GeForce RTX 30-series or newer (compute capability 8.6, 8.9, 12.0 or 12.1) " +
                           $"and an NVIDIA driver for CUDA 13 (580+); found {gpuName}" +
                           (gpu.ComputeCapabilityMajor > 0
                               ? $", compute capability {gpu.ComputeCapabilityMajor}.{gpu.ComputeCapabilityMinor}, " +
                                 $"driver CUDA {gpu.DriverCudaVersion / 1000}.{gpu.DriverCudaVersion % 1000 / 10}"
                               : "");
                return null;

            case "directml":
                if (!windows) return "DirectML is only available on Windows";
                if (!gpu.SupportsDirectMl)
                    return "DirectML needs Windows 10 1903 or later and a GPU with a Direct3D 12 driver";
                return null;

            case "coreml":
                if (!macos) return "CoreML is only available on macOS";
                if (!gpu.SupportsCoreMl) return "CoreML needs macOS 10.15 or later";
                return null;

            default:
                return "it is not a known inference device";
        }
    }

    /// <summary>
    /// What ONNX Runtime's Linux CUDA provider links against (from its ELF NEEDED entries). The mod
    /// downloads these on Windows only, so on Linux a missing one means the provider cannot load.
    /// </summary>
    private static List<string> MissingLinuxCudaLibraries(int major)
    {
        string[] libraries =
        {
            $"libcudart.so.{major}", $"libcublas.so.{major}", $"libcublasLt.so.{major}",
            "libcurand.so.10", major >= 13 ? "libcufft.so.12" : "libcufft.so.11", "libcudnn.so.9"
        };

        var missing = new List<string>();
        foreach (string library in libraries)
        {
            if (NativeLibrary.TryLoad(library, out IntPtr handle)) NativeLibrary.Free(handle);
            else missing.Add(library);
        }
        return missing;
    }
}
