using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using Vintagestory.API.Common;
using VSTerrainDiffusion.Core;

namespace VSTerrainDiffusion.Native;

/// <summary>
/// ONNX Runtime itself: which build each provider needs, where it comes from (NuGet, the GitHub
/// release, or AMD's wheel) and where it is kept.
/// </summary>
public static partial class OnnxRuntimeBootstrap
{
    /// <summary>The DirectML redistributable paired with the DirectML build of ONNX Runtime.</summary>
    private const string DirectMlVersion = "1.15.4";

    /// <summary>
    /// AMD's ONNX Runtime build with the MIGraphX provider, from the onnxruntime-migraphx wheel on
    /// PyPI, which is the same release as the managed binding. The wheel carries the C API runtime
    /// as well as the Python module, and the provider links against the system's ROCm 7.
    /// </summary>
    private const string RocmWheelUrl =
        "https://files.pythonhosted.org/packages/53/66/d0a9bcd1f261bff5f23d3dcc038c401f4251e9bd173911676f0b2a737a34/" +
        "onnxruntime_migraphx-1.24.4-cp312-cp312-manylinux_2_34_x86_64.whl";

    /// <summary>SHA-256 of <see cref="RocmWheelUrl"/>.</summary>
    private const string RocmWheelSha256 =
        "f2dee7e871a2e502492cf5f16f0bfc205190efd87cb8b4320056c3e5f833a911";

    /// <summary>What the ROCm provider library links against from the system's ROCm install.</summary>
    public static readonly string[] RocmSystemLibraries = { "libamdhip64.so.7", "libmigraphx_c.so.3" };

    /// <summary>The CUDA major version detected by <see cref="DetectCudaMajorVersion"/>, or -1 before the first call.</summary>
    private static int _cudaMajor = -1;

    /// <summary>Where a given ONNX Runtime build lives, without fetching anything.</summary>
    private static string RuntimeDirectoryFor(InferenceProvider provider)
    {
        string flavour = provider == InferenceProvider.Cuda
            ? "cuda" + DetectCudaMajorVersion()
            : provider.ToString().ToLowerInvariant();
        return Path.GetFullPath(
            Path.Combine(DiffusionPaths.RuntimeDirectory, OnnxRuntimeVersion, flavour, CurrentRid()));
    }

    /// <summary>
    /// Makes sure the ONNX Runtime build for <paramref name="provider"/> is complete on disk,
    /// downloading it into a staging directory and swapping it into place when it is not.
    /// </summary>
    /// <param name="provider">The ONNX Runtime build: CPU, CUDA, DirectML, CoreML or ROCm.</param>
    /// <param name="cudaExecutionProvider">
    /// Whether the CUDA execution provider itself will be used, as opposed to the CUDA build merely
    /// hosting the TensorRT RTX plugin. It decides whether the CUDA provider library is worth
    /// fetching.
    /// </param>
    /// <param name="logger">The server log.</param>
    /// <param name="cancellation">Stops the download.</param>
    /// <returns>The build's directory.</returns>
    /// <exception cref="InvalidOperationException">Downloads are off and the build is incomplete.</exception>
    private static string EnsureNativeFiles(InferenceProvider provider, bool cudaExecutionProvider,
                                            ILogger logger, CancellationToken cancellation)
    {
        string rid = CurrentRid();
        string directory = RuntimeDirectoryFor(provider);
        List<NativeSource> sources = SourcesFor(provider, rid, cudaExecutionProvider);

        if (HasCompleteRuntime(directory, sources)) return directory;

        if (!InferenceSettings.Current.DownloadRuntime)
        {
            throw new InvalidOperationException(
                $"Runtime downloads are disabled ({DownloadRuntimeCode}=false) and no complete ONNX Runtime was found in " +
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
                NativeDownloads.Extract(client, source, staging, logger, cancellation);

            if (!HasCompleteRuntime(staging, sources))
                throw new FileNotFoundException("ONNX Runtime native libraries are incomplete after download");
            NativeDownloads.ReplaceDirectory(staging, directory);
        }
        finally
        {
            NativeDownloads.TryDeleteDirectory(staging);
        }
        return directory;
    }

    /// <summary>The nuget.org flat-container URL of a package version.</summary>
    private static string NuGetUrl(string packageId, string version)
    {
        string id = packageId.ToLowerInvariant();
        return $"https://api.nuget.org/v3-flatcontainer/{id}/{version}/{id}.{version}.nupkg";
    }

    /// <summary>The URL of an asset of this ONNX Runtime version's GitHub release.</summary>
    private static string GitHubAssetUrl(string assetName) =>
        $"https://github.com/microsoft/onnxruntime/releases/download/v{OnnxRuntimeVersion}/{assetName}";

    /// <summary>The archives an ONNX Runtime build is assembled from, and which files each contributes.</summary>
    /// <param name="provider">The build.</param>
    /// <param name="rid">The .NET runtime identifier, e.g. linux-x64.</param>
    /// <param name="cudaExecutionProvider">Whether to include the CUDA provider library.</param>
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

            case InferenceProvider.Rocm when rid == "linux-x64":
                // A wheel, so a ZIP; pinned, so it is fetched whole and checked. The runtime inside
                // carries its version in the file name, and the managed binding loads the plain one.
                sources.Add(new NativeSource
                {
                    Name = $"onnxruntime-migraphx {OnnxRuntimeVersion}",
                    Url = RocmWheelUrl,
                    Kind = ArchiveKind.Zip,
                    Sha256 = RocmWheelSha256,
                    EntryPaths = new[]
                    {
                        $"onnxruntime/capi/libonnxruntime.so.{OnnxRuntimeVersion}",
                        "onnxruntime/capi/libonnxruntime_providers_shared.so",
                        "onnxruntime/capi/libonnxruntime_providers_migraphx.so"
                    },
                    TargetFileNames = new[]
                    {
                        "libonnxruntime.so",
                        "libonnxruntime_providers_shared.so",
                        "libonnxruntime_providers_migraphx.so"
                    }
                });
                break;

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
                // The macOS build has no shared-providers library: CoreML is compiled into the
                // runtime itself, and the package ships libonnxruntime.dylib alone.
                sources.Add(new NativeSource
                {
                    Name = "Microsoft.ML.OnnxRuntime",
                    Url = NuGetUrl("Microsoft.ML.OnnxRuntime", OnnxRuntimeVersion),
                    Kind = ArchiveKind.Zip,
                    EntryPaths = HostPlatform.IsMacOS
                        ? new[] { $"runtimes/{rid}/native/{PrimaryLibraryName()}" }
                        : new[]
                        {
                            $"runtimes/{rid}/native/{PrimaryLibraryName()}",
                            $"runtimes/{rid}/native/{ProvidersSharedLibraryName()}"
                        }
                });
                break;
        }
        return sources;
    }

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

        if (HostPlatform.IsWindows &&
            HostPlatform.Architecture == Architecture.X64 &&
            !HasSystemCudaLibraries())
        {
            _cudaMajor = PinnedWindowsCudaMajorVersion;
            return _cudaMajor;
        }

        foreach (int major in new[] { 13, 12 })
        {
            string name = HostPlatform.IsWindows ? $"cudart64_{major}.dll" : $"libcudart.so.{major}";
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

    /// <summary>The DirectML package's folder name for a runtime identifier.</summary>
    private static string DirectMlBinFolder(string rid) => rid switch
    {
        "win-arm64" => "arm64-win",
        _ => "x64-win"
    };

    /// <summary>
    /// Whether every file the sources contribute is in <paramref name="directory"/> and not empty.
    /// Sizes and hashes are not checked.
    /// </summary>
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

    /// <summary>The ONNX Runtime library's file name on this platform.</summary>
    private static string PrimaryLibraryName()
    {
        if (HostPlatform.IsWindows) return "onnxruntime.dll";
        if (HostPlatform.IsMacOS) return "libonnxruntime.dylib";
        return "libonnxruntime.so";
    }

    /// <summary>The shared-providers library's file name on this platform (none on macOS).</summary>
    private static string ProvidersSharedLibraryName()
    {
        if (HostPlatform.IsWindows) return "onnxruntime_providers_shared.dll";
        if (HostPlatform.IsMacOS) return "libonnxruntime_providers_shared.dylib";
        return "libonnxruntime_providers_shared.so";
    }

    /// <summary>This machine's .NET runtime identifier, e.g. linux-x64, win-x64 or osx-arm64.</summary>
    /// <exception cref="PlatformNotSupportedException">Neither x64 nor arm64.</exception>
    internal static string CurrentRid()
    {
        string os = HostPlatform.IsWindows ? "win" : HostPlatform.IsMacOS ? "osx" : "linux";
        string arch = HostPlatform.Architecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            _ => throw new PlatformNotSupportedException(
                "Terrain Diffusion needs an x64 or arm64 machine; found " + HostPlatform.Architecture)
        };
        return os + "-" + arch;
    }
}
