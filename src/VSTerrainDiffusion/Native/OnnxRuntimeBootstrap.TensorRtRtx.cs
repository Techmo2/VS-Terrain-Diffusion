using System;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using Vintagestory.API.Common;
using VSTerrainDiffusion.Core;
using VSTerrainDiffusion.Pipeline;

namespace VSTerrainDiffusion.Native;

/// <summary>
/// NVIDIA's TensorRT RTX execution provider: a plugin library registered on the CUDA build of ONNX
/// Runtime, with the TensorRT RTX libraries it links against (64-bit Linux and Windows).
/// </summary>
public static partial class OnnxRuntimeBootstrap
{
    /// <summary>TensorRT RTX version carried by the plugin wheels below.</summary>
    public const string TensorRtRtxVersion = "1.6.1";

    /// <summary>Version of the onnxruntime-ep-nv-tensorrt-rtx plugin release.</summary>
    private const string TensorRtRtxPluginVersion = "0.4.0";

    /// <summary>The plugin's manylinux wheel.</summary>
    private const string LinuxTensorRtRtxWheelUrl =
        "https://files.pythonhosted.org/packages/9c/68/093626a054300fa7f4f1a6d18b775d185034c6bb7fb056ac4f9d1e9b50d4/" +
        "onnxruntime_ep_nv_tensorrt_rtx_cu13-0.4.0-py3-none-manylinux_2_28_x86_64.whl";

    /// <summary>SHA-256 of <see cref="LinuxTensorRtRtxWheelUrl"/>.</summary>
    private const string LinuxTensorRtRtxWheelSha256 =
        "68b8f7306b50cf76daf997b8be24ec3813498c15d48e0ddd211db14f2a9daa19";

    /// <summary>Size of <see cref="LinuxTensorRtRtxWheelUrl"/>, for the download notice.</summary>
    private const long LinuxTensorRtRtxWheelBytes = 143733764;

    /// <summary>The plugin's win_amd64 wheel.</summary>
    private const string WindowsTensorRtRtxWheelUrl =
        "https://files.pythonhosted.org/packages/0c/7d/7dae1810328c4ddc611129b66fa11edd14c8c814ac1b206c3fac481541af/" +
        "onnxruntime_ep_nv_tensorrt_rtx_cu13-0.4.0-py3-none-win_amd64.whl";

    /// <summary>SHA-256 of <see cref="WindowsTensorRtRtxWheelUrl"/>.</summary>
    private const string WindowsTensorRtRtxWheelSha256 =
        "8aa0db63e31384024409818789eaa8ad136f0e83681dda204b7e3d178df2ff69";

    /// <summary>Size of <see cref="WindowsTensorRtRtxWheelUrl"/>, for the download notice.</summary>
    private const long WindowsTensorRtRtxWheelBytes = 104947525;

    /// <summary>Whether this machine takes the Windows build of the plugin.</summary>
    private static bool WindowsTensorRtRtx => HostPlatform.IsWindows;

    /// <summary>This platform's wheel.</summary>
    private static string TensorRtRtxWheelUrl =>
        WindowsTensorRtRtx ? WindowsTensorRtRtxWheelUrl : LinuxTensorRtRtxWheelUrl;

    /// <summary>SHA-256 of this platform's wheel.</summary>
    private static string TensorRtRtxWheelSha256 =>
        WindowsTensorRtRtx ? WindowsTensorRtRtxWheelSha256 : LinuxTensorRtRtxWheelSha256;

    /// <summary>Size of this platform's wheel.</summary>
    private static long TensorRtRtxWheelBytes =>
        WindowsTensorRtRtx ? WindowsTensorRtRtxWheelBytes : LinuxTensorRtRtxWheelBytes;

    /// <summary>The plugin library ORT registers, named for the platform it was built for.</summary>
    private static string TensorRtRtxProviderLibrary => WindowsTensorRtRtx
        ? "onnxruntime_providers_nv_tensorrt_rtx.dll"
        : "libonnxruntime_providers_nv_tensorrt_rtx.so";

    /// <summary>The EP's registration name, which is also the EpName it reports back to ORT.</summary>
    internal const string TensorRtRtxEpName = "NvTensorRTRTX";

    /// <summary>The plugin library ORT loads, plus the libraries it links against, from the manylinux wheel.</summary>
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

    /// <summary>The plugin and its libraries for this platform, as a pinned set.</summary>
    private static readonly PinnedFileSet TensorRtRtxFiles = new("TensorRT RTX runtime",
        WindowsTensorRtRtx ? WindowsTensorRtRtxRuntimeFiles : LinuxTensorRtRtxRuntimeFiles,
        VerificationFileName,
        TensorRtRtxWheelSha256 + "  onnxruntime-ep-nv-tensorrt-rtx-cu13.whl\n");

    /// <summary>
    /// Loaded by absolute path before the plugin, so the loader resolves them by SONAME (Linux) or
    /// by module base name (Windows) without LD_LIBRARY_PATH or a PATH entry.
    /// </summary>
    private static string[] TensorRtRtxDependencies => WindowsTensorRtRtx
        ? WindowsTensorRtRtxDependencies
        : LinuxTensorRtRtxDependencies;

    /// <summary>What the Linux plugin links against, each loaded before it.</summary>
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

    /// <summary>Serialises preparation across worlds.</summary>
    private static readonly object TensorRtRtxGate = new();

    /// <summary>Where the plugin was prepared; kept for the life of the game.</summary>
    private static string _tensorRtRtxDirectory;

    /// <summary>Directory holding the TensorRT RTX plugin provider, once initialised.</summary>
    public static string TensorRtRtxDirectory => _tensorRtRtxDirectory;

    /// <summary>
    /// Downloads the TensorRT RTX plugin provider and its libraries. The worker loads them and
    /// registers the plugin once its ONNX Runtime is loaded.
    /// </summary>
    /// <returns>The plugin's directory.</returns>
    /// <exception cref="PlatformNotSupportedException">Not 64-bit Linux or Windows.</exception>
    private static string PrepareTensorRtRtx(ILogger logger, CancellationToken cancellation)
    {
        lock (TensorRtRtxGate)
        {
            if (_tensorRtRtxDirectory != null) return _tensorRtRtxDirectory;
            if (!(HostPlatform.IsLinux || HostPlatform.IsWindows) ||
                HostPlatform.Architecture != Architecture.X64)
                throw new PlatformNotSupportedException("The TensorRT RTX provider requires 64-bit Linux or Windows");

            string directory = Path.Combine(
                DiffusionPaths.RuntimeDirectory, "tensorrt-rtx", TensorRtRtxPluginVersion, CurrentRid());
            EnsurePinned(TensorRtRtxFiles, directory, () =>
            {
                LoadingNotice.Post(logger,
                    "Downloading the TensorRT RTX inference runtime ({0}). This happens once.",
                    ModelAssetManager.HumanBytes(TensorRtRtxWheelBytes));
                using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
                TensorRtRtxFiles.Install(directory, staging => ExtractTensorRtRtx(client, staging, logger, cancellation),
                    "The verified TensorRT RTX wheel did not contain a complete runtime");
            });

            _tensorRtRtxDirectory = directory;
            logger.Notification("[{0}] TensorRT RTX {1} provider prepared in {2}",
                DiffusionPaths.ModId, TensorRtRtxVersion, directory);
            return directory;
        }
    }

    /// <summary>Downloads the pinned wheel and takes the plugin's files out of its package folder.</summary>
    private static void ExtractTensorRtRtx(HttpClient client, string staging, ILogger logger,
                                           CancellationToken cancellation)
    {
        int count = TensorRtRtxFiles.Files.Count;
        var entries = new string[count];
        var names = new string[count];
        for (int i = 0; i < count; i++)
        {
            names[i] = TensorRtRtxFiles.Files[i].Name;
            entries[i] = "onnxruntime_ep_nv_tensorrt_rtx/" + names[i];
        }

        NativeDownloads.ExtractVerifiedZip(client, new NativeSource
        {
            Name = $"TensorRT RTX {TensorRtRtxVersion} provider (NVIDIA)",
            Url = TensorRtRtxWheelUrl,
            Kind = ArchiveKind.Zip,
            Sha256 = TensorRtRtxWheelSha256,
            EntryPaths = entries,
            TargetFileNames = names
        }, staging, logger, cancellation);
    }
}
