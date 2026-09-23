using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using Microsoft.ML.OnnxRuntime;
using Vintagestory.API.Common;
using VSTerrainDiffusion.Core;

namespace VSTerrainDiffusion.Native;

/// <summary>
/// Which execution provider the ONNX sessions should be created with.
/// </summary>
public enum InferenceProvider
{
    Cpu,
    Cuda,
    DirectMl,
    CoreMl,
    OpenVino,

    /// <summary>
    /// NVIDIA's TensorRT for RTX, an ONNX Runtime plugin provider. Ampere and later; builds its
    /// engines on the machine, in seconds, and caches them.
    /// </summary>
    TensorRtRtx
}

/// <summary>
/// Locates (and, if needed, downloads) the native ONNX Runtime that matches this machine and the
/// configured execution provider, then teaches the runtime's P/Invoke layer where to find it.
///
/// The mod ships only the ~1 MB managed binding; native libraries are pulled from pinned official
/// packages on first use. ZIP packages are range-read so only the required entries are transferred.
/// </summary>
public static class OnnxRuntimeBootstrap
{
    /// <summary>ONNX Runtime version. Must match the Microsoft.ML.OnnxRuntime.Managed package reference.</summary>
    public const string OnnxRuntimeVersion = "1.24.4";

    private const string DirectMlVersion = "1.15.4";
    // Later releases inspect host cache topology through sysfs and can crash when a restricted
    // container exposes sibling CPU IDs outside its virtual CPU set. The 2022.3 LTS runtime uses
    // /proc and process affinity instead, and supports both decoder graphs used by the mod.
    public const string OpenVinoVersion = "2022.3.2";
    private const string OpenVinoArchiveUrl =
        "https://storage.openvinotoolkit.org/repositories/openvino/packages/2022.3.2/linux/" +
        "l_openvino_toolkit_rhel8_2022.3.2.9279.e2c7e4d7b4d_x86_64.tgz";
    private const string OpenVinoWheelUrl =
        "https://files.pythonhosted.org/packages/df/6f/44de968108af6194e3a156493a2ab0003a03d51b5dfff9853cee055aa922/" +
        "openvino-2022.3.2-9279-cp310-cp310-manylinux2014_x86_64.whl";
    private const string OpenVinoArchiveSha256 =
        "21968bc27dc15463004706fd7d4efa28445fd544e3e977e7580cb7ce1b3ba830";
    private const string OpenVinoWheelSha256 =
        "ab4a54f0841bdcfe5fd2c654a4a8c0257991aa2aeaf65fe9498cd6b7a20bd84d";
    private const string OpenVinoVerificationFileName = ".sources.sha256";
    private const string OpenVinoVerificationContents =
        OpenVinoArchiveSha256 + "  openvino-rhel8.tgz\n" +
        OpenVinoWheelSha256 + "  openvino-manylinux.whl\n";
    private static readonly (string Name, long Size, string Sha256)[] OpenVinoRuntimeFiles =
    {
        ("libopenvino_c.so.2232", 457176,
            "e6af0851acb20bee97c46cdbe81dfa24f746ec1b27798b85ff73bb594be43c98"),
        ("libopenvino.so.2232", 18474168,
            "57cbe0668c5fd0aaf214b768b32f051b258ef79d652e25d5dc7173fbde1101ea"),
        ("libopenvino_onnx_frontend.so.2232", 5192296,
            "e48e169f3c1877d41ca6e42bcf32f6e2d096e9f62324c1f9d761382a13444fd3"),
        ("libopenvino_intel_cpu_plugin.so", 41362344,
            "eecf898422c883393004300b1dc3c876c6083b8697b2739a5c832af9e0f398ed"),
        ("libtbb.so.2", 261488,
            "dc72de1a3de811d973cd72ebd068767a19a8f9e6a211e5d864da23d9004eb54e"),
        ("libtbbbind.so.2", 78008,
            "c36624ea9065d0e21eb669933bd91a528fefc1f2d61474d4fa5b861837bde927"),
        ("libhwloc.so.5", 254200,
            "278cc4cd04939fd7d4591872a88fffc5b8d9490e0345d5d0d42816e1e6b3ea0a"),
        ("libpugixml.so.1", 249128,
            "ba546fe4e42eb07a14421e4e79fc74ae50b61be30a86e7b3451a2c7eee6339c7"),
        ("plugins.xml", 753,
            "45bb98cee3bf8d8c51499c7e9a3ad789b181a8878e7919cca866e52175782cb2")
    };

    /// <summary>TensorRT RTX version carried by the plugin wheels below.</summary>
    public const string TensorRtRtxVersion = "1.6.1";

    private const string TensorRtRtxPluginVersion = "0.4.0";

    private const string LinuxTensorRtRtxWheelUrl =
        "https://files.pythonhosted.org/packages/9c/68/093626a054300fa7f4f1a6d18b775d185034c6bb7fb056ac4f9d1e9b50d4/" +
        "onnxruntime_ep_nv_tensorrt_rtx_cu13-0.4.0-py3-none-manylinux_2_28_x86_64.whl";
    private const string LinuxTensorRtRtxWheelSha256 =
        "68b8f7306b50cf76daf997b8be24ec3813498c15d48e0ddd211db14f2a9daa19";
    private const long LinuxTensorRtRtxWheelBytes = 143733764;

    private const string WindowsTensorRtRtxWheelUrl =
        "https://files.pythonhosted.org/packages/0c/7d/7dae1810328c4ddc611129b66fa11edd14c8c814ac1b206c3fac481541af/" +
        "onnxruntime_ep_nv_tensorrt_rtx_cu13-0.4.0-py3-none-win_amd64.whl";
    private const string WindowsTensorRtRtxWheelSha256 =
        "8aa0db63e31384024409818789eaa8ad136f0e83681dda204b7e3d178df2ff69";
    private const long WindowsTensorRtRtxWheelBytes = 104947525;

    private const string TensorRtRtxVerificationFileName = ".sources.sha256";

    private static bool WindowsTensorRtRtx => OperatingSystem.IsWindows();

    private static string TensorRtRtxWheelUrl =>
        WindowsTensorRtRtx ? WindowsTensorRtRtxWheelUrl : LinuxTensorRtRtxWheelUrl;

    private static string TensorRtRtxWheelSha256 =>
        WindowsTensorRtRtx ? WindowsTensorRtRtxWheelSha256 : LinuxTensorRtRtxWheelSha256;

    private static long TensorRtRtxWheelBytes =>
        WindowsTensorRtRtx ? WindowsTensorRtRtxWheelBytes : LinuxTensorRtRtxWheelBytes;

    private static string TensorRtRtxVerificationContents =>
        TensorRtRtxWheelSha256 + "  onnxruntime-ep-nv-tensorrt-rtx-cu13.whl\n";

    /// <summary>The plugin library ORT registers, named for the platform it was built for.</summary>
    private static string TensorRtRtxProviderLibrary => WindowsTensorRtRtx
        ? "onnxruntime_providers_nv_tensorrt_rtx.dll"
        : "libonnxruntime_providers_nv_tensorrt_rtx.so";

    /// <summary>The EP's registration name, which is also the EpName it reports back to ORT.</summary>
    internal const string TensorRtRtxEpName = "NvTensorRTRTX";

    /// <summary>
    /// The CUDA maths libraries ONNX Runtime's CUDA provider links against, which are shipped with
    /// the CUDA toolkit rather than with the display driver. Named for the directory they are
    /// cached in, so a change here fetches a fresh set rather than mixing two.
    /// </summary>
    private const string CudaLibrariesVersion = "cuda13.4-cudnn9.26";

    /// <summary>The CUDA major version of the set above, which the ORT build has to agree with.</summary>
    private const int PinnedWindowsCudaMajorVersion = 13;

    private const string CudaRedistBase =
        "https://developer.download.nvidia.com/compute/cuda/redist/";
    private const string CudnnRedistBase =
        "https://developer.download.nvidia.com/compute/cudnn/redist/";

    private const string CublasArchive = "libcublas-windows-x86_64-13.8.0.4-archive";
    private const string CufftArchive = "libcufft-windows-x86_64-12.4.0.43-archive";
    private const string NvrtcArchive = "cuda_nvrtc-windows-x86_64-13.4.92-archive";
    private const string CudnnArchive = "cudnn-windows-x86_64-9.26.0.51_cuda13-archive";

    /// <summary>Bytes actually transferred, which is what the player waits for.</summary>
    private const long CudaLibrariesDownloadBytes = 1046000000;

    private const string CudaLibrariesMarkerFileName = ".sources";
    private const string CudaLibrariesMarkerContents =
        CublasArchive + "\n" + CufftArchive + "\n" +
        NvrtcArchive + "\n" + CudnnArchive + "\n";

    /// <summary>
    /// What onnxruntime_providers_cuda.dll imports, plus what those libraries load in turn. cuDNN
    /// is a loader stub over a set of engine libraries it opens by bare name at runtime, and its
    /// runtime-compiled engine needs NVRTC, so the whole set is preloaded by absolute path. CUDA's
    /// own runtime is not here: ONNX Runtime 1.24 links cudart statically.
    /// </summary>
    private static readonly (string Name, long Size, string Sha256)[] CudaLibraryFiles =
    {
        ("cublasLt64_13.dll", 493474416,
            "cad63434448e7141629e240ea093ad596a7ef6a0f67b468ba9bc1df6e1eeee33"),
        ("cublas64_13.dll", 54873200,
            "60bbba8868290311e9c1657b2193ddec667744eb555ff843f87acb7c039f9efa"),
        ("cufft64_12.dll", 232604272,
            "d353fd42da7729709864731f35888aa177793d2350e56557df4c6ebaaf079ace"),
        ("nvrtc-builtins64_134.dll", 7243376,
            "73768e90526e8a43d5eb356f0ffad198468e8c15e95d45a84c6c4b12fb9aa02f"),
        ("nvrtc64_130_0.dll", 106038896,
            "c92027e5eae58b6f272cafb438bf59b55deb02bd326b183956e84e0a180dbd14"),
        ("cudnn_engines_precompiled64_9.dll", 224516720,
            "c289ff5cab82e2be6a2ad116f966f4c655c94e11731e146bc3963d809eb46fc8"),
        ("cudnn_engines_runtime_compiled64_9.dll", 32993904,
            "30a222f73ea8b89c216cc5384080addff3632a77ae6eed0d1eaa87fb57d9b184"),
        ("cudnn_engines_tensor_ir64_9.dll", 156272,
            "b97ef392f51923ee8dc616dca596fbc6f979281b135eb76991e8cadd43c6294f"),
        ("cudnn_heuristic64_9.dll", 73166960,
            "7ff562ba7530ff390575abd62794338565d397cd5627760bb94028e6d256012d"),
        ("cudnn_ops64_9.dll", 37461616,
            "bf813ebb0a8c1e09dd95499ac701b5e45e3d066d5624b2716a289ed5a2ca1c28"),
        ("cudnn_adv64_9.dll", 105227888,
            "235134b2f410569d3517da2df3cccc5bc8b3ed8466bb49c2503702598d2bf12a"),
        ("cudnn_cnn64_9.dll", 1533040,
            "f479a762f5e8b10ae737a8a279cef630e41ce4d4b32618f1fd9f943f61ff18d5"),
        ("cudnn_graph64_9.dll", 116192880,
            "8f98ea372de52da536559b538f84739b0f1c50ae7f32a07e7fd111f4938fb641"),
        ("cudnn_ext64_9.dll", 130160,
            "ba3a74ab3996caee8ca893825db87b27e97f00ab14a13f7e985fd2ebc7683ea0"),
        ("cudnn64_9.dll", 270448,
            "1a0728a6bd5c704bcdd1347cc6127fb6085fce773aaf9f1824281e9846c7b090")
    };

    /// <summary>
    /// The four the provider imports outright. If a machine already resolves all of them - a CUDA
    /// toolkit and cuDNN are installed - there is nothing to fetch.
    /// </summary>
    private static readonly string[] CudaLibraryImports =
    {
        "cublas64_13.dll", "cublasLt64_13.dll", "cufft64_12.dll", "cudnn64_9.dll"
    };

    /// <summary>The plugin library ORT loads, plus the libraries it links against, from that wheel.</summary>
    private static (string Name, long Size, string Sha256)[] TensorRtRtxRuntimeFiles =>
        WindowsTensorRtRtx ? WindowsTensorRtRtxRuntimeFiles : LinuxTensorRtRtxRuntimeFiles;

    private static readonly (string Name, long Size, string Sha256)[] LinuxTensorRtRtxRuntimeFiles =
    {
        ("libonnxruntime_providers_nv_tensorrt_rtx.so", 5441968,
            "f07c9ea8f9ea9eabc80f24c8532a11308d17069ea7d5975b6a12e18b82972a44"),
        ("libtensorrt_rtx.so.1.6.1", 227372352,
            "42cb00a71e684828cdb3a7af029a9328463df0f8d2163bed12cb3cf7a98cd5da"),
        ("libtensorrt_onnxparser_rtx.so.1.6.1", 3856320,
            "297ab8bae3622d5751e8ed15898088340c697af19364abcfddc0e2add6437d60"),
        ("libtensorrt_plugins.so", 69602904,
            "6d0fabea24978f57f9e6bb9eff18d2f35b5b6d4799a7092947895d4ee905f2b8"),
        ("libtensorrt_shim.so", 1525840,
            "6780468474c8b1af44e964d22c06b290398334a656b2ce76bef62d4b7255379a"),
        ("libcudart.so.13.4.36", 798496,
            "82d50fc923566a86ff5449cb190d58007a26280669b4f62be2c284cacb51ed24")
    };

    /// <summary>
    /// The same provider from the win_amd64 wheel of the same plugin release. Windows has no
    /// separate shim library, and the versioned libraries carry the version in the file name
    /// rather than in an SONAME suffix.
    /// </summary>
    private static readonly (string Name, long Size, string Sha256)[] WindowsTensorRtRtxRuntimeFiles =
    {
        ("onnxruntime_providers_nv_tensorrt_rtx.dll", 2274416,
            "22e572703579a56778b077fb3b0024ae974997b29c16d833a148599d9aca3db2"),
        ("tensorrt_rtx_1_6.dll", 229530736,
            "77f050f7cec29fa03fa749964d01a69c815dc4f7f31e8cbaf311a2b9d24e52c8"),
        ("tensorrt_onnxparser_rtx_1_6.dll", 2306672,
            "a13e8a21884275972a12b677e26444102d5a1e31c6bf98e1898833e6c7eb938d"),
        ("tensorrt_plugins.dll", 48753264,
            "62431cb52b36bd84a33527297bbc221bfb969d74baf66f82c1f67a488f5df24d"),
        ("cudart64_13.dll", 551024,
            "f0e947c8e46b5b3b38ae643d5aff5b939d25676b7fe7c299bc2cc9a05815fda6")
    };

    /// <summary>
    /// Loaded by absolute path before the plugin, so the loader resolves them by SONAME (Linux) or
    /// by module base name (Windows) without LD_LIBRARY_PATH or a PATH entry.
    /// </summary>
    private static string[] TensorRtRtxDependencies => WindowsTensorRtRtx
        ? WindowsTensorRtRtxDependencies
        : LinuxTensorRtRtxDependencies;

    private static readonly string[] LinuxTensorRtRtxDependencies =
    {
        "libcudart.so.13.4.36",
        "libtensorrt_rtx.so.1.6.1",
        "libtensorrt_onnxparser_rtx.so.1.6.1"
    };

    /// <summary>
    /// Windows has no RUNPATH, so the plugin's own directory is not searched for the libraries it
    /// loads by bare name. tensorrt_plugins.dll is in the list for that reason: TensorRT opens it
    /// itself when it builds an engine, well after the plugin was registered.
    /// </summary>
    private static readonly string[] WindowsTensorRtRtxDependencies =
    {
        "cudart64_13.dll",
        "tensorrt_rtx_1_6.dll",
        "tensorrt_onnxparser_rtx_1_6.dll",
        "tensorrt_plugins.dll"
    };

    private static readonly object Gate = new();
    private static readonly object OpenVinoGate = new();
    private static readonly object TensorRtRtxGate = new();
    private static readonly object CudaLibrariesGate = new();
    private static readonly List<IntPtr> TensorRtRtxHandles = new();
    private static readonly List<IntPtr> CudaLibraryHandles = new();
    private static bool _cudaLibrariesInitialised;
    private static string _cudaLibrariesDirectory;
    private static bool _initialised;
    private static bool _openVinoInitialised;
    private static bool _tensorRtRtxInitialised;
    private static bool _deviceChangeReported;
    private static string _nativeDirectory;

    /// <summary>
    /// The directory the P/Invoke resolver was pinned to, or null while nothing has been installed.
    /// Only the TensorRT RTX path installs it before the runtime is known to work.
    /// </summary>
    private static string _resolverDirectory;
    private static string _openVinoDirectory;
    private static string _tensorRtRtxDirectory;

    /// <summary>The provider that was actually resolved, available after <see cref="Initialize"/>.</summary>
    public static InferenceProvider Provider { get; private set; } = InferenceProvider.Cpu;

    /// <summary>Whether this run fetched a runtime, rather than finding one already on disk.</summary>
    public static bool Downloaded { get; private set; }

    /// <summary>Directory containing the resolved native libraries.</summary>
    public static string NativeDirectory => _nativeDirectory;

    /// <summary>Native ONNX Runtime version selected for the active provider.</summary>
    public static string ActiveRuntimeVersion => OnnxRuntimeVersion;

    /// <summary>Human-readable runtime combination used in the status command.</summary>
    public static string ActiveRuntimeDescription => Provider switch
    {
        InferenceProvider.OpenVino =>
            $"OpenVINO {OpenVinoVersion} decoder, ONNX Runtime {OnnxRuntimeVersion} coarse/base",
        InferenceProvider.TensorRtRtx =>
            $"TensorRT RTX {TensorRtRtxVersion} on ONNX Runtime {OnnxRuntimeVersion}",
        _ => $"ONNX Runtime {ActiveRuntimeVersion}"
    };

    /// <summary>Directory containing the standalone OpenVINO C runtime, if initialised.</summary>
    public static string OpenVinoDirectory => _openVinoDirectory;

    /// <summary>
    /// Download and initialise the standalone OpenVINO C runtime used for decoder inference.
    /// It is separate from ONNX Runtime and does not change <see cref="Provider"/>.
    /// </summary>
    public static string InitializeOpenVino(ILogger logger, CancellationToken cancellation = default)
    {
        if (_openVinoInitialised) return _openVinoDirectory;
        lock (OpenVinoGate)
        {
            if (_openVinoInitialised) return _openVinoDirectory;
            if (!OperatingSystem.IsLinux() || RuntimeInformation.OSArchitecture != Architecture.X64)
                throw new PlatformNotSupportedException("The standalone OpenVINO runtime requires 64-bit Linux");

            string directory = Path.Combine(
                DiffusionPaths.RuntimeDirectory, "openvino", OpenVinoVersion, "linux-x64");
            bool filesPresent = HasStandaloneOpenVinoRuntime(directory);
            bool verified = filesPresent && HasVerifiedOpenVinoRuntime(directory);
            if (!verified)
            {
                if (!DiffusionConfig.Instance.DownloadRuntime)
                {
                    // An administrator may deliberately provide the native files themselves. An
                    // auto-downloaded install, identified by its marker, must match the pinned
                    // sources rather than silently accepting a stale or interrupted install.
                    if (!filesPresent || File.Exists(Path.Combine(directory, OpenVinoVerificationFileName)))
                    {
                        throw new InvalidOperationException(
                            "Runtime downloads are disabled and no verified standalone OpenVINO runtime was found in " +
                            directory);
                    }
                }
                else
                {
                    Downloaded = true;
                    LoadingNotice.Post(logger, "Downloading the OpenVINO inference runtime. This happens once.");
                    using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
                    InstallStandaloneOpenVino(client, directory, logger, cancellation);
                }
            }

            if (!HasStandaloneOpenVinoRuntime(directory))
                throw new FileNotFoundException("Standalone OpenVINO native libraries are missing in " + directory);

            _openVinoDirectory = directory;
            _openVinoInitialised = true;
            logger.Notification("[{0}] OpenVINO {1} standalone CPU runtime prepared in {2}",
                DiffusionPaths.ModId, OpenVinoVersion, directory);
            return directory;
        }
    }

    /// <summary>
    /// Downloads the plugin provider, loads its libraries and registers it. Must run after the ONNX
    /// Runtime resolver is installed: registering a plugin goes through the native runtime.
    /// </summary>
    public static string InitializeTensorRtRtx(ILogger logger, CancellationToken cancellation = default)
    {
        if (_tensorRtRtxInitialised) return _tensorRtRtxDirectory;
        lock (TensorRtRtxGate)
        {
            if (_tensorRtRtxInitialised) return _tensorRtRtxDirectory;
            if (!(OperatingSystem.IsLinux() || OperatingSystem.IsWindows()) ||
                RuntimeInformation.OSArchitecture != Architecture.X64)
                throw new PlatformNotSupportedException("The TensorRT RTX provider requires 64-bit Linux or Windows");

            string directory = Path.Combine(
                DiffusionPaths.RuntimeDirectory, "tensorrt-rtx", TensorRtRtxPluginVersion, CurrentRid());
            bool filesPresent = HasTensorRtRtxRuntime(directory);
            if (!filesPresent || !HasVerifiedTensorRtRtxRuntime(directory))
            {
                if (!DiffusionConfig.Instance.DownloadRuntime)
                {
                    if (!filesPresent || File.Exists(Path.Combine(directory, TensorRtRtxVerificationFileName)))
                    {
                        throw new InvalidOperationException(
                            "Runtime downloads are disabled and no verified TensorRT RTX provider was found in " +
                            directory);
                    }
                }
                else
                {
                    Downloaded = true;
                    LoadingNotice.Post(logger,
                        "Downloading the TensorRT RTX inference runtime ({0}). This happens once.",
                        Pipeline.ModelAssetManager.HumanBytes(TensorRtRtxWheelBytes));
                    using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
                    InstallTensorRtRtx(client, directory, logger, cancellation);
                }
            }

            if (!HasTensorRtRtxRuntime(directory))
                throw new FileNotFoundException("TensorRT RTX native libraries are missing in " + directory);

            foreach (string dependency in TensorRtRtxDependencies)
                TensorRtRtxHandles.Add(NativeLibrary.Load(Path.Combine(directory, dependency)));

            OrtEnv.Instance().RegisterExecutionProviderLibrary(
                TensorRtRtxEpName, Path.Combine(directory, TensorRtRtxProviderLibrary));

            _tensorRtRtxDirectory = directory;
            _tensorRtRtxInitialised = true;
            logger.Notification("[{0}] TensorRT RTX {1} provider registered from {2}",
                DiffusionPaths.ModId, TensorRtRtxVersion, directory);
            return directory;
        }
    }

    /// <summary>
    /// Makes the CUDA maths libraries available to ONNX Runtime's CUDA provider on Windows, where
    /// nothing else on a gaming machine supplies them. Does nothing on other platforms, which get
    /// them from the system CUDA install the way this mod has always expected, and nothing on a
    /// Windows machine that already has a toolkit and cuDNN.
    ///
    /// Like the TensorRT RTX plugin, the libraries are loaded by absolute path before the provider
    /// is created: cuDNN opens its engine libraries by bare name, and this directory is not on any
    /// search path the loader would consult for those.
    /// </summary>
    public static string InitializeCudaLibraries(ILogger logger, CancellationToken cancellation = default)
    {
        if (!OperatingSystem.IsWindows() || RuntimeInformation.OSArchitecture != Architecture.X64) return null;
        if (_cudaLibrariesInitialised) return _cudaLibrariesDirectory;

        lock (CudaLibrariesGate)
        {
            if (_cudaLibrariesInitialised) return _cudaLibrariesDirectory;

            if (HasSystemCudaLibraries())
            {
                logger.Notification(
                    "[{0}] Using the CUDA libraries already installed on this machine; nothing to download.",
                    DiffusionPaths.ModId);
                _cudaLibrariesInitialised = true;
                return null;
            }

            string directory = Path.Combine(
                DiffusionPaths.RuntimeDirectory, "cuda-libraries", CudaLibrariesVersion, CurrentRid());
            bool filesPresent = HasCudaLibraries(directory);
            if (!filesPresent || !HasVerifiedCudaLibraries(directory))
            {
                if (!DiffusionConfig.Instance.DownloadRuntime)
                {
                    if (!filesPresent || File.Exists(Path.Combine(directory, CudaLibrariesMarkerFileName)))
                    {
                        throw new InvalidOperationException(
                            "Runtime downloads are disabled and no verified CUDA support libraries were found in " +
                            directory);
                    }
                }
                else
                {
                    Downloaded = true;
                    LoadingNotice.Post(logger,
                        "Downloading the CUDA support libraries ({0}). This happens once.",
                        Pipeline.ModelAssetManager.HumanBytes(CudaLibrariesDownloadBytes));
                    using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(60) };
                    InstallCudaLibraries(client, directory, logger, cancellation);
                }
            }

            if (!HasCudaLibraries(directory))
                throw new FileNotFoundException("CUDA support libraries are missing in " + directory);

            foreach ((string name, _, _) in CudaLibraryFiles)
                CudaLibraryHandles.Add(NativeLibrary.Load(Path.Combine(directory, name)));

            _cudaLibrariesDirectory = directory;
            _cudaLibrariesInitialised = true;
            logger.Notification("[{0}] CUDA support libraries ({1}) loaded from {2}",
                DiffusionPaths.ModId, CudaLibrariesVersion, directory);
            return directory;
        }
    }

    /// <summary>Whether this machine already resolves everything the CUDA provider imports.</summary>
    private static bool HasSystemCudaLibraries()
    {
        var handles = new List<IntPtr>();
        try
        {
            foreach (string name in CudaLibraryImports)
            {
                if (!NativeLibrary.TryLoad(name, out IntPtr handle)) return false;
                handles.Add(handle);
            }
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            // Only probing. The provider loads them for real later, from wherever they were found.
            foreach (IntPtr handle in handles) NativeLibrary.Free(handle);
        }
    }

    private static void InstallCudaLibraries(HttpClient client, string directory, ILogger logger,
                                             CancellationToken cancellation)
    {
        string parent = Path.GetDirectoryName(directory)
                        ?? throw new InvalidOperationException("CUDA library directory has no parent");
        Directory.CreateDirectory(parent);

        string staging = directory + ".install-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(staging);
        try
        {
            // These archives run to gigabytes, nearly all of it static libraries and headers, so
            // they are read entry by entry rather than downloaded and hashed whole. Every file that
            // comes out is checked against the pinned manifest below, which is the same guarantee.
            foreach (NativeSource source in CudaLibrarySources())
                ExtractFromZip(client, source, staging, logger, cancellation);

            if (!HasCudaLibraries(staging))
                throw new FileNotFoundException("The CUDA redistributables did not contain a complete library set");

            File.WriteAllText(Path.Combine(staging, CudaLibrariesMarkerFileName), CudaLibrariesMarkerContents);
            if (!HasVerifiedCudaLibraries(staging))
                throw new InvalidDataException(
                    "The extracted CUDA support libraries did not match the pinned file manifest");
            ReplaceDirectory(staging, directory);
        }
        finally
        {
            TryDeleteDirectory(staging);
        }
    }

    private static IEnumerable<NativeSource> CudaLibrarySources()
    {
        yield return CudaRedistSource(CudaRedistBase + "libcublas/windows-x86_64/", CublasArchive,
            "cuBLAS", "cublas64_13.dll", "cublasLt64_13.dll");
        yield return CudaRedistSource(CudaRedistBase + "libcufft/windows-x86_64/", CufftArchive,
            "cuFFT", "cufft64_12.dll");
        yield return CudaRedistSource(CudaRedistBase + "cuda_nvrtc/windows-x86_64/", NvrtcArchive,
            "NVRTC", "nvrtc64_130_0.dll", "nvrtc-builtins64_134.dll");
        yield return CudaRedistSource(CudnnRedistBase + "cudnn/windows-x86_64/", CudnnArchive, "cuDNN",
            "cudnn64_9.dll", "cudnn_adv64_9.dll", "cudnn_cnn64_9.dll", "cudnn_engines_precompiled64_9.dll",
            "cudnn_engines_runtime_compiled64_9.dll", "cudnn_engines_tensor_ir64_9.dll", "cudnn_ext64_9.dll",
            "cudnn_graph64_9.dll", "cudnn_heuristic64_9.dll", "cudnn_ops64_9.dll");
    }

    private static NativeSource CudaRedistSource(string baseUrl, string archive, string name,
                                                 params string[] fileNames) => new()
    {
        Name = name + " (NVIDIA CUDA redistributable)",
        Url = baseUrl + archive + ".zip",
        Kind = ArchiveKind.Zip,
        FileNames = fileNames,
        EntryPaths = Array.ConvertAll(fileNames, n => archive + "/bin/x64/" + n)
    };

    private static bool HasCudaLibraries(string directory)
    {
        foreach ((string name, _, _) in CudaLibraryFiles)
        {
            if (!File.Exists(Path.Combine(directory, name))) return false;
        }
        return true;
    }

    private static bool HasVerifiedCudaLibraries(string directory)
    {
        string marker = Path.Combine(directory, CudaLibrariesMarkerFileName);
        try
        {
            if (!File.Exists(marker) ||
                !string.Equals(File.ReadAllText(marker), CudaLibrariesMarkerContents, StringComparison.Ordinal))
                return false;

            foreach ((string name, long size, string expectedSha256) in CudaLibraryFiles)
            {
                string path = Path.Combine(directory, name);
                var info = new FileInfo(path);
                if (!info.Exists || info.Length != size) return false;
                using FileStream input = File.OpenRead(path);
                string actualSha256 = Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
                if (!string.Equals(actualSha256, expectedSha256, StringComparison.Ordinal)) return false;
            }
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Directory holding the TensorRT RTX plugin provider, once initialised.</summary>
    public static string TensorRtRtxDirectory => _tensorRtRtxDirectory;

    /// <summary>Where TensorRT RTX keeps the engines it builds for this machine.</summary>
    public static string TensorRtRtxCacheDirectory =>
        Path.Combine(DiffusionPaths.OptimizedModelDirectory, "tensorrt-rtx", TensorRtRtxVersion);

    /// <summary>
    /// Resolves the native runtime and installs the DllImport resolver. Blocking; may download
    /// tens of megabytes (or a few hundred for CUDA). Safe to call more than once.
    /// </summary>
    public static void Initialize(ILogger logger, CancellationToken cancellation = default)
    {
        if (_initialised)
        {
            WarnIfDeviceChanged(logger);
            return;
        }
        lock (Gate)
        {
            if (_initialised)
            {
                WarnIfDeviceChanged(logger);
                return;
            }

            InferenceProvider provider = ResolveRequestedProvider(DiffusionConfig.Instance.InferenceDevice, logger);
            // OpenVINO runs beside an ORT CPU build; TensorRT RTX is a plugin on the CUDA build.
            InferenceProvider onnxProvider = OnnxProviderFor(provider);
            string directory;

            try
            {
                directory = EnsureNativeFiles(
                    onnxProvider, provider == InferenceProvider.Cuda, logger, cancellation);
                if (provider == InferenceProvider.OpenVino) InitializeOpenVino(logger, cancellation);
                if (provider == InferenceProvider.Cuda) InitializeCudaLibraries(logger, cancellation);
                if (provider == InferenceProvider.TensorRtRtx)
                {
                    InstallResolver(directory);   // the plugin registers through the native runtime
                    InitializeTensorRtRtx(logger, cancellation);
                }
            }
            catch (Exception e) when (provider != InferenceProvider.Cpu && e is not OperationCanceledException)
            {
                InferenceProvider fallback = FallbackFor(provider, logger);
                logger.Warning("[{0}] Could not prepare the {1} runtime: {2}",
                    DiffusionPaths.ModId, provider, e.Message);
                logger.Warning("[{0}] The selected framework {1} is not supported. Changing {1} to {2} in the config.",
                    DiffusionPaths.ModId, DeviceName(provider), DeviceName(fallback));
                logger.Warning("[{0}] Keep the effective provider fixed for an established world because provider " +
                               "changes can alter newly generated terrain slightly.", DiffusionPaths.ModId);
                DiffusionConfig.PersistInferenceDevice(DeviceName(fallback), logger);

                onnxProvider = OnnxProviderFor(fallback);
                // The P/Invoke resolver pins one native directory for the life of the process, and
                // the TensorRT RTX path installs it before the plugin can register. A fallback that
                // needs a different ONNX Runtime build therefore cannot be honoured here at all -
                // only by the next start, which the config now points at.
                if (_resolverDirectory != null &&
                    !string.Equals(_resolverDirectory, RuntimeDirectoryFor(onnxProvider),
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw DiffusionFailure.Fatal(logger,
                        $"The {DeviceName(provider)} inference device is not usable on this machine. The config now " +
                        $"asks for {DeviceName(fallback)}, which needs a different ONNX Runtime than the one this " +
                        "session already loaded. Start the game again to come up on it.", e);
                }

                provider = fallback;
                directory = EnsureNativeFiles(
                    onnxProvider, provider == InferenceProvider.Cuda, logger, cancellation);
                if (provider == InferenceProvider.Cuda) InitializeCudaLibraries(logger, cancellation);
            }

            _nativeDirectory = directory;
            Provider = provider;
            InstallResolver(directory);
            _initialised = true;

            logger.Notification("[{0}] ONNX Runtime {1} ({2}) loaded from {3}",
                DiffusionPaths.ModId, OnnxRuntimeVersion, onnxProvider, directory);
        }
    }

    /// <summary>
    /// The native runtime is resolved once per process: its library directory is fixed the moment
    /// the P/Invoke resolver is installed. A device changed between two worlds in one session
    /// therefore does nothing until the game restarts, which is worth saying rather than leaving
    /// the player to infer it from <c>/tdiff status</c>.
    /// </summary>
    private static void WarnIfDeviceChanged(ILogger logger)
    {
        if (_deviceChangeReported) return;
        InferenceProvider requested = ResolveRequestedProvider(DiffusionConfig.Instance.InferenceDevice, logger);
        if (requested == Provider) return;

        _deviceChangeReported = true;
        logger.Warning(
            "[{0}] inference device is now '{1}' but this session already started on {2}. The native runtime is " +
            "loaded once per process, so restart the game for the change to take effect; this world will use {2}.",
            DiffusionPaths.ModId, DiffusionConfig.Instance.InferenceDevice, Provider);
    }

    /// <summary>
    /// Which ONNX Runtime build a provider runs on. OpenVINO runs beside an ORT CPU build, and
    /// TensorRT RTX is a plugin on the CUDA one.
    /// </summary>
    private static InferenceProvider OnnxProviderFor(InferenceProvider provider) => provider switch
    {
        InferenceProvider.OpenVino => InferenceProvider.Cpu,
        InferenceProvider.TensorRtRtx => InferenceProvider.Cuda,
        _ => provider
    };

    /// <summary>
    /// Where a provider goes when its runtime cannot be prepared: whatever this machine would have
    /// picked for itself, which is CoreML on macOS, DirectML on 64-bit Windows, CUDA on Linux with
    /// an NVIDIA driver, and ONNX Runtime CPU everywhere else.
    ///
    /// Naming a provider here instead would assume hardware the player may not have - a fallback
    /// fixed at CUDA lands an AMD machine on a provider it cannot run at all. The automatic choice
    /// is the one place in the mod that already knows what this machine has. When it is the
    /// provider that just failed, the CPU is what is left.
    /// </summary>
    private static InferenceProvider FallbackFor(InferenceProvider provider, ILogger logger)
    {
        // OpenVINO is already an ONNX Runtime CPU session everywhere except the decoder, so the
        // CPU is where it degrades to - not a GPU the player did not ask this world to be built on.
        if (provider == InferenceProvider.OpenVino) return InferenceProvider.Cpu;

        InferenceProvider automatic = ResolveRequestedProvider("auto", logger);
        return automatic == provider ? InferenceProvider.Cpu : automatic;
    }

    /// <summary>The provider's <c>inferenceDevice</c> spelling, as the config file writes it.</summary>
    private static string DeviceName(InferenceProvider provider) => provider switch
    {
        InferenceProvider.Cuda => "cuda",
        InferenceProvider.DirectMl => "directml",
        InferenceProvider.CoreMl => "coreml",
        InferenceProvider.OpenVino => "openvino",
        InferenceProvider.TensorRtRtx => "tensorrt-rtx",
        _ => "cpu"
    };

    private static InferenceProvider ResolveRequestedProvider(string configured, ILogger logger)
    {
        bool windows = OperatingSystem.IsWindows();
        bool linux = OperatingSystem.IsLinux();
        bool macos = OperatingSystem.IsMacOS();
        bool x64 = RuntimeInformation.OSArchitecture == Architecture.X64;

        switch (configured)
        {
            case "cpu":
                return InferenceProvider.Cpu;
            case "cuda":
                return InferenceProvider.Cuda;
            case "directml":
            case "dml":
                return InferenceProvider.DirectMl;
            case "coreml":
                return InferenceProvider.CoreMl;
            case "openvino":
                if (linux && x64) return InferenceProvider.OpenVino;
                logger.Warning("[{0}] inference device 'openvino' is only available on 64-bit Linux.",
                    DiffusionPaths.ModId);
                return InferenceProvider.Cpu;
            case "tensorrt-rtx":
                if ((linux || windows) && x64 && HasNvidiaDriver()) return InferenceProvider.TensorRtRtx;
                logger.Warning("[{0}] inference device 'tensorrt-rtx' needs 64-bit Windows or Linux with an NVIDIA " +
                               "driver and a GeForce RTX 30xx or newer GPU.", DiffusionPaths.ModId);
                // Windows always has DirectML, which is a GPU path on any vendor and much closer to
                // what was asked for than dropping the whole session onto the CPU.
                return windows && x64 ? InferenceProvider.DirectMl : InferenceProvider.Cpu;
            case "gpu":
            case "auto":
            default:
                if (macos) return InferenceProvider.CoreMl;
                if (windows && x64) return InferenceProvider.DirectMl;
                if (linux && x64 && HasNvidiaDriver()) return InferenceProvider.Cuda;
                if (configured == "gpu")
                {
                    logger.Warning("[{0}] inference device 'gpu' requested but no GPU provider is available on this platform.",
                        DiffusionPaths.ModId);
                }
                return InferenceProvider.Cpu;
        }
    }

    /// <summary>Cheap heuristic so 'auto' does not pull a 200 MB CUDA package onto AMD/Intel machines.</summary>
    private static bool HasNvidiaDriver()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                // The display driver puts nvcuda.dll in the system directory. This asks about the
                // driver, not the CUDA toolkit: TensorRT RTX brings its own CUDA runtime, and the
                // toolkit is not installed on a normal gaming machine.
                if (!NativeLibrary.TryLoad("nvcuda.dll", out IntPtr driver)) return false;
                NativeLibrary.Free(driver);
                return true;
            }

            if (Directory.Exists("/proc/driver/nvidia")) return true;
            foreach (string candidate in new[] { "/dev/nvidiactl", "/dev/nvidia0" })
                if (File.Exists(candidate)) return true;
        }
        catch { /* treat probe failures as "no GPU" */ }
        return false;
    }

    private enum ArchiveKind
    {
        /// <summary>A ZIP (NuGet package or GitHub .zip asset) read with range requests.</summary>
        Zip,

        /// <summary>A GitHub .tar.gz asset, which has to be streamed in full.</summary>
        TarGz
    }

    private sealed class NativeSource
    {
        public string Name;
        public string Url;
        public ArchiveKind Kind;
        public string Sha256;

        /// <summary>Exact archive paths for <see cref="ArchiveKind.Zip"/>; ignored for tarballs.</summary>
        public string[] EntryPaths;

        /// <summary>
        /// Optional destination names corresponding to <see cref="EntryPaths"/>. Used when an
        /// archive only contains a versioned library name but the native binding loads its stable
        /// name.
        /// </summary>
        public string[] TargetFileNames;

        /// <summary>File names to pull out of a tarball.</summary>
        public string[] FileNames;
    }

    /// <summary>
    /// Where a given ONNX Runtime build lives, without fetching anything. Separate from
    /// <see cref="EnsureNativeFiles"/> so a fallback can be tested against the directory the
    /// resolver is already pinned to before any of it is downloaded.
    /// </summary>
    private static string RuntimeDirectoryFor(InferenceProvider provider)
    {
        string flavour = provider == InferenceProvider.Cuda
            ? "cuda" + DetectCudaMajorVersion()
            : provider.ToString().ToLowerInvariant();
        return Path.GetFullPath(
            Path.Combine(DiffusionPaths.RuntimeDirectory, OnnxRuntimeVersion, flavour, CurrentRid()));
    }

    /// <param name="cudaExecutionProvider">
    /// Whether the CUDA execution provider itself will be used, as opposed to the CUDA build merely
    /// hosting the TensorRT RTX plugin. It decides whether the CUDA provider library is worth
    /// fetching.
    /// </param>
    private static string EnsureNativeFiles(InferenceProvider provider, bool cudaExecutionProvider,
                                            ILogger logger, CancellationToken cancellation)
    {
        string rid = CurrentRid();
        string directory = RuntimeDirectoryFor(provider);
        List<NativeSource> sources = SourcesFor(provider, rid, cudaExecutionProvider);

        if (HasCompleteRuntime(directory, sources)) return directory;

        if (!DiffusionConfig.Instance.DownloadRuntime)
        {
            throw new InvalidOperationException(
                "Runtime downloads are disabled (downloadRuntime=false) and no complete ONNX Runtime was found in " +
                directory);
        }

        string parent = Path.GetDirectoryName(directory)
                        ?? throw new InvalidOperationException("ONNX Runtime directory has no parent");
        Directory.CreateDirectory(parent);
        string staging = directory + ".install-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(staging);

        // Another first-run download the player is waiting on, so it goes on the loading screen too.
        Downloaded = true;
        LoadingNotice.Post(logger, "Downloading the {0} inference runtime. This happens once.",
            provider.ToString().ToUpperInvariant());

        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
            foreach (NativeSource source in sources)
            {
                if (source.Kind == ArchiveKind.TarGz)
                    ExtractFromTarGz(client, source, staging, logger, cancellation);
                else
                    ExtractFromZip(client, source, staging, logger, cancellation);
            }

            if (!HasCompleteRuntime(staging, sources))
                throw new FileNotFoundException("ONNX Runtime native libraries are incomplete after download");
            ReplaceDirectory(staging, directory);
        }
        finally
        {
            TryDeleteDirectory(staging);
        }
        return directory;
    }

    private static void ExtractFromZip(HttpClient client, NativeSource source, string directory,
                                       ILogger logger, CancellationToken cancellation)
    {
        if (source.TargetFileNames != null && source.TargetFileNames.Length != source.EntryPaths.Length)
        {
            throw new InvalidDataException($"{source.Name} has mismatched archive and destination file lists");
        }

        if (!string.IsNullOrEmpty(source.Sha256))
        {
            ExtractVerifiedZip(client, source, directory, logger, cancellation);
            return;
        }

        List<RemoteZipExtractor.Entry> entries = RemoteZipExtractor.ReadCentralDirectory(client, source.Url, cancellation);

        for (int i = 0; i < source.EntryPaths.Length; i++)
        {
            string wanted = source.EntryPaths[i];
            RemoteZipExtractor.Entry entry = entries.Find(e =>
                string.Equals(e.Name, wanted, StringComparison.OrdinalIgnoreCase));
            if (entry == null)
            {
                throw new FileNotFoundException($"{source.Name} does not contain {wanted}");
            }

            string targetName = source.TargetFileNames == null
                ? Path.GetFileName(entry.Name)
                : source.TargetFileNames[i];
            string target = Path.Combine(directory, targetName);
            if (File.Exists(target) && new FileInfo(target).Length == entry.UncompressedSize) continue;

            logger.Notification("[{0}] Fetching {1} ({2}) from {3}",
                DiffusionPaths.ModId, Path.GetFileName(entry.Name),
                Pipeline.ModelAssetManager.HumanBytes(entry.UncompressedSize), source.Name);
            RemoteZipExtractor.ExtractEntry(client, source.Url, entry, target, cancellation);
        }
    }

    private static void ExtractFromTarGz(HttpClient client, NativeSource source, string directory,
                                         ILogger logger, CancellationToken cancellation)
    {
        logger.Notification("[{0}] Downloading {1}; this archive cannot be partially fetched, so the whole file is streamed.",
            DiffusionPaths.ModId, source.Name);

        List<string> written = RemoteTarGzExtractor.Extract(
            client, source.Url, source.Sha256, source.FileNames, source.TargetFileNames, directory, cancellation);
        IReadOnlyList<string> expected = source.TargetFileNames ?? source.FileNames;
        foreach (string name in expected)
        {
            if (!written.Contains(name) && !File.Exists(Path.Combine(directory, name)))
            {
                throw new FileNotFoundException($"{source.Name} does not contain {name}");
            }
        }
    }

    private static void InstallStandaloneOpenVino(HttpClient client, string directory, ILogger logger,
                                                  CancellationToken cancellation)
    {
        string parent = Path.GetDirectoryName(directory)
                        ?? throw new InvalidOperationException("OpenVINO runtime directory has no parent");
        Directory.CreateDirectory(parent);

        string staging = directory + ".install-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(staging);
        try
        {
            foreach (NativeSource source in StandaloneOpenVinoSources())
            {
                if (source.Kind == ArchiveKind.TarGz)
                    ExtractFromTarGz(client, source, staging, logger, cancellation);
                else
                    ExtractFromZip(client, source, staging, logger, cancellation);
            }

            if (!HasStandaloneOpenVinoRuntime(staging))
                throw new FileNotFoundException("The verified OpenVINO archives did not contain a complete runtime");

            File.WriteAllText(
                Path.Combine(staging, OpenVinoVerificationFileName), OpenVinoVerificationContents);
            if (!HasVerifiedOpenVinoRuntime(staging))
                throw new InvalidDataException(
                    "The extracted OpenVINO runtime did not match the pinned file manifest");
            ReplaceDirectory(staging, directory);
        }
        finally
        {
            TryDeleteDirectory(staging);
        }
    }

    private static void InstallTensorRtRtx(HttpClient client, string directory, ILogger logger,
                                           CancellationToken cancellation)
    {
        string parent = Path.GetDirectoryName(directory)
                        ?? throw new InvalidOperationException("TensorRT RTX directory has no parent");
        Directory.CreateDirectory(parent);

        string staging = directory + ".install-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(staging);
        try
        {
            var entries = new string[TensorRtRtxRuntimeFiles.Length];
            var names = new string[TensorRtRtxRuntimeFiles.Length];
            for (int i = 0; i < TensorRtRtxRuntimeFiles.Length; i++)
            {
                names[i] = TensorRtRtxRuntimeFiles[i].Name;
                entries[i] = "onnxruntime_ep_nv_tensorrt_rtx/" + names[i];
            }

            ExtractVerifiedZip(client, new NativeSource
            {
                Name = $"TensorRT RTX {TensorRtRtxVersion} provider (NVIDIA)",
                Url = TensorRtRtxWheelUrl,
                Kind = ArchiveKind.Zip,
                Sha256 = TensorRtRtxWheelSha256,
                EntryPaths = entries,
                TargetFileNames = names
            }, staging, logger, cancellation);

            if (!HasTensorRtRtxRuntime(staging))
                throw new FileNotFoundException("The verified TensorRT RTX wheel did not contain a complete runtime");

            File.WriteAllText(
                Path.Combine(staging, TensorRtRtxVerificationFileName), TensorRtRtxVerificationContents);
            if (!HasVerifiedTensorRtRtxRuntime(staging))
                throw new InvalidDataException(
                    "The extracted TensorRT RTX runtime did not match the pinned file manifest");
            ReplaceDirectory(staging, directory);
        }
        finally
        {
            TryDeleteDirectory(staging);
        }
    }

    private static bool HasTensorRtRtxRuntime(string directory)
    {
        foreach ((string name, _, _) in TensorRtRtxRuntimeFiles)
        {
            if (!File.Exists(Path.Combine(directory, name))) return false;
        }
        return true;
    }

    private static bool HasVerifiedTensorRtRtxRuntime(string directory)
    {
        string marker = Path.Combine(directory, TensorRtRtxVerificationFileName);
        try
        {
            if (!File.Exists(marker) ||
                !string.Equals(File.ReadAllText(marker), TensorRtRtxVerificationContents, StringComparison.Ordinal))
                return false;

            foreach ((string name, long size, string expectedSha256) in TensorRtRtxRuntimeFiles)
            {
                string path = Path.Combine(directory, name);
                var info = new FileInfo(path);
                if (!info.Exists || info.Length != size) return false;
                using FileStream input = File.OpenRead(path);
                string actualSha256 = Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
                if (!string.Equals(actualSha256, expectedSha256, StringComparison.Ordinal)) return false;
            }
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void ExtractVerifiedZip(HttpClient client, NativeSource source, string directory,
                                           ILogger logger, CancellationToken cancellation)
    {
        logger.Notification("[{0}] Downloading and verifying {1}; the complete archive is required for SHA-256 validation.",
            DiffusionPaths.ModId, source.Name);

        string archivePath = Path.Combine(directory, "." + Guid.NewGuid().ToString("N") + ".download");
        try
        {
            DownloadVerifiedFile(client, source.Url, source.Sha256, archivePath, cancellation);

            using var archive = new ZipArchive(File.OpenRead(archivePath), ZipArchiveMode.Read);
            var selected = new ZipArchiveEntry[source.EntryPaths.Length];
            for (int i = 0; i < source.EntryPaths.Length; i++)
            {
                selected[i] = archive.GetEntry(source.EntryPaths[i]);
                if (selected[i] == null)
                    throw new FileNotFoundException($"{source.Name} does not contain {source.EntryPaths[i]}");
            }

            for (int i = 0; i < selected.Length; i++)
            {
                ZipArchiveEntry entry = selected[i];
                string targetName = source.TargetFileNames == null
                    ? Path.GetFileName(entry.FullName)
                    : source.TargetFileNames[i];
                string target = Path.Combine(directory, targetName);
                string temporary = target + ".tmp";
                try
                {
                    using (Stream input = entry.Open())
                    using (var output = new FileStream(
                               temporary, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
                    {
                        input.CopyTo(output);
                        output.Flush(flushToDisk: true);
                    }
                    if (new FileInfo(temporary).Length != entry.Length)
                        throw new InvalidDataException($"{source.Name} produced an incomplete {entry.FullName}");
                    File.Move(temporary, target, overwrite: true);
                }
                finally
                {
                    if (File.Exists(temporary)) File.Delete(temporary);
                }
            }
        }
        finally
        {
            if (File.Exists(archivePath)) File.Delete(archivePath);
        }
    }

    private static void DownloadVerifiedFile(HttpClient client, string url, string expectedSha256,
                                             string destination, CancellationToken cancellation)
    {
        using HttpResponseMessage response = client
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellation)
            .GetAwaiter().GetResult();
        response.EnsureSuccessStatusCode();

        using Stream input = response.Content.ReadAsStream(cancellation);
        using var output = new FileStream(
            destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20,
            FileOptions.SequentialScan);
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1 << 20];
        while (true)
        {
            int read = input.ReadAsync(buffer.AsMemory(), cancellation).AsTask().GetAwaiter().GetResult();
            if (read == 0) break;
            hash.AppendData(buffer, 0, read);
            output.Write(buffer, 0, read);
        }
        output.Flush(flushToDisk: true);

        string actualSha256 = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        if (!string.Equals(actualSha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Downloaded archive SHA-256 is {actualSha256}, expected {expectedSha256}");
        }
    }

    private static void ReplaceDirectory(string staging, string destination)
    {
        string backup = destination + ".previous-" + Guid.NewGuid().ToString("N");
        bool backedUp = false;
        if (Directory.Exists(destination))
        {
            Directory.Move(destination, backup);
            backedUp = true;
        }

        try
        {
            Directory.Move(staging, destination);
        }
        catch
        {
            if (backedUp && !Directory.Exists(destination) && Directory.Exists(backup))
                Directory.Move(backup, destination);
            throw;
        }

        if (backedUp) TryDeleteDirectory(backup);
    }

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static string NuGetUrl(string packageId, string version)
    {
        string id = packageId.ToLowerInvariant();
        return $"https://api.nuget.org/v3-flatcontainer/{id}/{version}/{id}.{version}.nupkg";
    }

    private static string GitHubAssetUrl(string assetName) =>
        $"https://github.com/microsoft/onnxruntime/releases/download/v{OnnxRuntimeVersion}/{assetName}";

    private static List<NativeSource> SourcesFor(InferenceProvider provider, string rid,
                                                 bool cudaExecutionProvider)
    {
        var sources = new List<NativeSource>();
        switch (provider)
        {
            case InferenceProvider.Cuda when rid is "linux-x64" or "win-x64":
            {
                int cudaMajor = DetectCudaMajorVersion();
                bool windows = rid == "win-x64";

                // TensorRT RTX runs on this build too, and never touches the CUDA provider - a
                // fifth of a gigabyte that would only sit on disk. Fetch it for the CUDA provider
                // itself, which is the one thing that loads it.
                string[] core = windows
                    ? new[] { "onnxruntime.dll", "onnxruntime_providers_shared.dll" }
                    : new[] { "libonnxruntime.so", "libonnxruntime_providers_shared.so" };
                string cudaProvider = windows
                    ? "onnxruntime_providers_cuda.dll"
                    : "libonnxruntime_providers_cuda.so";
                var wanted = new List<string>(core);
                if (cudaExecutionProvider) wanted.Add(cudaProvider);
                string[] fileNames = wanted.ToArray();

                if (cudaMajor >= 13)
                {
                    // NuGet only ships the CUDA 12 build, so the CUDA 13 flavour comes from the
                    // GitHub release assets instead.
                    string platform = windows ? "win-x64" : "linux-x64";
                    string extension = windows ? "zip" : "tgz";
                    string asset = $"onnxruntime-{platform}-gpu_cuda13-{OnnxRuntimeVersion}.{extension}";
                    // Only the Windows asset is read by entry path, and the folder inside it drops
                    // the _cuda13 the file name carries. The tarball is matched by file name, so the
                    // Linux root is never used.
                    string root = windows
                        ? $"onnxruntime-win-x64-gpu-{OnnxRuntimeVersion}"
                        : $"onnxruntime-{platform}-gpu_cuda13-{OnnxRuntimeVersion}";

                    sources.Add(new NativeSource
                    {
                        Name = asset,
                        Url = GitHubAssetUrl(asset),
                        Kind = windows ? ArchiveKind.Zip : ArchiveKind.TarGz,
                        FileNames = fileNames,
                        EntryPaths = Array.ConvertAll(fileNames, n => $"{root}/lib/{n}")
                    });
                }
                else
                {
                    string packageId = windows
                        ? "Microsoft.ML.OnnxRuntime.Gpu.Windows"
                        : "Microsoft.ML.OnnxRuntime.Gpu.Linux";
                    sources.Add(new NativeSource
                    {
                        Name = packageId,
                        Url = NuGetUrl(packageId, OnnxRuntimeVersion),
                        Kind = ArchiveKind.Zip,
                        FileNames = fileNames,
                        EntryPaths = Array.ConvertAll(fileNames, n => $"runtimes/{rid}/native/{n}")
                    });
                }
                break;
            }

            case InferenceProvider.DirectMl:
                sources.Add(new NativeSource
                {
                    Name = "Microsoft.ML.OnnxRuntime.DirectML",
                    Url = NuGetUrl("Microsoft.ML.OnnxRuntime.DirectML", OnnxRuntimeVersion),
                    Kind = ArchiveKind.Zip,
                    EntryPaths = new[]
                    {
                        $"runtimes/{rid}/native/onnxruntime.dll",
                        $"runtimes/{rid}/native/onnxruntime_providers_shared.dll"
                    }
                });
                sources.Add(new NativeSource
                {
                    Name = "Microsoft.AI.DirectML",
                    Url = NuGetUrl("Microsoft.AI.DirectML", DirectMlVersion),
                    Kind = ArchiveKind.Zip,
                    EntryPaths = new[] { $"bin/{DirectMlBinFolder(rid)}/DirectML.dll" }
                });
                break;

            default:
                sources.Add(new NativeSource
                {
                    Name = "Microsoft.ML.OnnxRuntime",
                    Url = NuGetUrl("Microsoft.ML.OnnxRuntime", OnnxRuntimeVersion),
                    Kind = ArchiveKind.Zip,
                    EntryPaths = new[]
                    {
                        $"runtimes/{rid}/native/{PrimaryLibraryName()}",
                        $"runtimes/{rid}/native/{ProvidersSharedLibraryName()}"
                    }
                });
                break;
        }
        return sources;
    }

    private static IReadOnlyList<NativeSource> StandaloneOpenVinoSources() => new NativeSource[]
    {
        new()
        {
            Name = $"OpenVINO {OpenVinoVersion} (Intel/RHEL 8)",
            Url = OpenVinoArchiveUrl,
            Kind = ArchiveKind.TarGz,
            Sha256 = OpenVinoArchiveSha256,
            FileNames = new[]
            {
                "LICENSE",
                "runtime-third-party-programs.txt",
                "onednn_third-party-programs.txt",
                "tbb_third-party-programs.txt",
                "libopenvino_c.so.2022.3.2",
                "libopenvino.so.2022.3.2",
                "libopenvino_onnx_frontend.so.2022.3.2",
                "libopenvino_intel_cpu_plugin.so",
                "plugins.xml"
            },
            TargetFileNames = new[]
            {
                "LICENSE.txt",
                "runtime-third-party-programs.txt",
                "onednn-third-party-programs.txt",
                "onetbb-third-party-programs.txt",
                "libopenvino_c.so.2232",
                "libopenvino.so.2232",
                "libopenvino_onnx_frontend.so.2232",
                "libopenvino_intel_cpu_plugin.so",
                "plugins.xml"
            }
        },
        new()
        {
            Name = $"OpenVINO {OpenVinoVersion} dependencies (Intel/PyPI)",
            Url = OpenVinoWheelUrl,
            Kind = ArchiveKind.Zip,
            Sha256 = OpenVinoWheelSha256,
            EntryPaths = new[]
            {
                "openvino/libs/libtbb.so.2",
                "openvino/libs/libtbbbind.so.2",
                "openvino/libs/libhwloc.so.5",
                "openvino/libs/libpugixml.so.1"
            }
        }
    };

    private static int _cudaMajor = -1;

    /// <summary>
    /// Which CUDA runtime the ONNX Runtime build has to match, since it ships separate builds for
    /// CUDA 12 and 13 and loading the wrong one fails with a missing cublasLt. Returns 12 when
    /// nothing is found, which is the more common configuration.
    ///
    /// On Windows a machine with no CUDA libraries of its own gets the ones this mod pins, and
    /// those are CUDA 13. Probing the machine there would answer for libraries that are about to
    /// be supplied rather than the ones that will actually be loaded.
    /// </summary>
    internal static int DetectCudaMajorVersion()
    {
        if (_cudaMajor > 0) return _cudaMajor;

        if (OperatingSystem.IsWindows() &&
            RuntimeInformation.OSArchitecture == Architecture.X64 &&
            !HasSystemCudaLibraries())
        {
            _cudaMajor = PinnedWindowsCudaMajorVersion;
            return _cudaMajor;
        }

        foreach (int major in new[] { 13, 12 })
        {
            string name = OperatingSystem.IsWindows() ? $"cudart64_{major}.dll" : $"libcudart.so.{major}";
            if (NativeLibrary.TryLoad(name, out IntPtr handle))
            {
                NativeLibrary.Free(handle);
                _cudaMajor = major;
                return major;
            }
        }

        _cudaMajor = 12;
        return _cudaMajor;
    }

    private static string DirectMlBinFolder(string rid) => rid switch
    {
        "win-arm64" => "arm64-win",
        _ => "x64-win"
    };

    private static bool HasCompleteRuntime(string directory, IReadOnlyList<NativeSource> sources)
    {
        if (!Directory.Exists(directory)) return false;
        foreach (NativeSource source in sources)
        {
            string[] fileNames = source.TargetFileNames ?? (source.Kind == ArchiveKind.TarGz
                ? source.FileNames
                : Array.ConvertAll(source.EntryPaths, Path.GetFileName));
            foreach (string fileName in fileNames)
            {
                var file = new FileInfo(Path.Combine(directory, fileName));
                if (!file.Exists || file.Length == 0) return false;
            }
        }
        return true;
    }

    private static bool HasStandaloneOpenVinoRuntime(string directory)
    {
        foreach ((string name, _, _) in OpenVinoRuntimeFiles)
        {
            if (!File.Exists(Path.Combine(directory, name))) return false;
        }
        return true;
    }

    private static bool HasVerifiedOpenVinoRuntime(string directory)
    {
        string marker = Path.Combine(directory, OpenVinoVerificationFileName);
        try
        {
            if (!File.Exists(marker) ||
                !string.Equals(File.ReadAllText(marker), OpenVinoVerificationContents, StringComparison.Ordinal))
                return false;

            foreach ((string name, long size, string expectedSha256) in OpenVinoRuntimeFiles)
            {
                string path = Path.Combine(directory, name);
                var info = new FileInfo(path);
                if (!info.Exists || info.Length != size) return false;
                using FileStream input = File.OpenRead(path);
                string actualSha256 = Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
                if (!string.Equals(actualSha256, expectedSha256, StringComparison.Ordinal)) return false;
            }
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string PrimaryLibraryName()
    {
        if (OperatingSystem.IsWindows()) return "onnxruntime.dll";
        if (OperatingSystem.IsMacOS()) return "libonnxruntime.dylib";
        return "libonnxruntime.so";
    }

    private static string ProvidersSharedLibraryName()
    {
        if (OperatingSystem.IsWindows()) return "onnxruntime_providers_shared.dll";
        if (OperatingSystem.IsMacOS()) return "libonnxruntime_providers_shared.dylib";
        return "libonnxruntime_providers_shared.so";
    }

    internal static string CurrentRid()
    {
        string os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux";
        string arch = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            _ => throw new PlatformNotSupportedException(
                "Terrain Diffusion needs an x64 or arm64 machine; found " + RuntimeInformation.OSArchitecture)
        };
        // ONNX Runtime does not publish an osx-x64 build for every release; arm64 is the supported Mac target.
        if (os == "osx" && arch == "x64") arch = "x64";
        return os + "-" + arch;
    }

    private static void InstallResolver(string directory)
    {
        NativeLibraryResolver.ConfigureOnnxRuntime(directory);
        _resolverDirectory = Path.GetFullPath(directory);
    }
}
