using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using Vintagestory.API.Common;
using VSTerrainDiffusion.Core;
using VSTerrainDiffusion.Pipeline;

namespace VSTerrainDiffusion.Native;

/// <summary>
/// The CUDA maths libraries ONNX Runtime's CUDA provider links against, which on Windows nothing else
/// on a gaming machine supplies: NVIDIA's redistributable cuBLAS, cuFFT, NVRTC and cuDNN.
/// </summary>
public static partial class OnnxRuntimeBootstrap
{
    /// <summary>
    /// The CUDA maths libraries ONNX Runtime's CUDA provider links against, which are shipped with
    /// the CUDA toolkit rather than with the display driver. Named for the directory they are
    /// cached in, so a change here fetches a fresh set rather than mixing two.
    /// </summary>
    private const string CudaLibrariesVersion = "cuda13.4-cudnn9.26";

    /// <summary>The CUDA major version of the set above, which the ORT build has to agree with.</summary>
    private const int PinnedWindowsCudaMajorVersion = 13;

    /// <summary>NVIDIA's CUDA redistributable archive root.</summary>
    private const string CudaRedistBase =
        "https://developer.download.nvidia.com/compute/cuda/redist/";

    /// <summary>NVIDIA's cuDNN redistributable archive root.</summary>
    private const string CudnnRedistBase =
        "https://developer.download.nvidia.com/compute/cudnn/redist/";

    /// <summary>cuBLAS archive name, without extension.</summary>
    private const string CublasArchive = "libcublas-windows-x86_64-13.8.0.4-archive";

    /// <summary>cuFFT archive name, without extension.</summary>
    private const string CufftArchive = "libcufft-windows-x86_64-12.4.0.43-archive";

    /// <summary>NVRTC archive name, without extension.</summary>
    private const string NvrtcArchive = "cuda_nvrtc-windows-x86_64-13.4.92-archive";

    /// <summary>cuDNN archive name, without extension.</summary>
    private const string CudnnArchive = "cudnn-windows-x86_64-9.26.0.51_cuda13-archive";

    /// <summary>Bytes actually transferred, which is what the player waits for.</summary>
    private const long CudaLibrariesDownloadBytes = 1046000000;

    /// <summary>
    /// Marker file of the set. Its own name, not <see cref="VerificationFileName"/>: it lists the
    /// archives rather than hashes, since they are range-read, not downloaded whole.
    /// </summary>
    private const string CudaLibrariesMarkerFileName = ".sources";

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

    /// <summary>The CUDA libraries as a pinned set.</summary>
    private static readonly PinnedFileSet CudaLibraries = new("CUDA support libraries", CudaLibraryFiles,
        CudaLibrariesMarkerFileName,
        CublasArchive + "\n" + CufftArchive + "\n" + NvrtcArchive + "\n" + CudnnArchive + "\n");

    /// <summary>Serialises preparation across worlds.</summary>
    private static readonly object CudaLibrariesGate = new();

    /// <summary>
    /// The libraries to preload, once prepared; empty when the machine has its own. Kept for the life
    /// of the game: verifying a gigabyte again for every world would be wasted.
    /// </summary>
    private static string[] _cudaLibrariesPreload;

    /// <summary>
    /// Makes the CUDA maths libraries available to ONNX Runtime's CUDA provider on Windows. Does
    /// nothing on other platforms, which get them from the system CUDA install, and nothing on a
    /// Windows machine that already has a toolkit and cuDNN.
    /// </summary>
    /// <remarks>
    /// Like the TensorRT RTX plugin, the worker loads the returned libraries by absolute path before
    /// the provider is created: cuDNN opens its engine libraries by bare name, and this directory is
    /// not on any search path the loader would consult for those.
    /// </remarks>
    /// <returns>Absolute paths to preload, in load order; empty when nothing is needed.</returns>
    private static string[] PrepareCudaLibraries(ILogger logger, CancellationToken cancellation)
    {
        if (!HostPlatform.IsWindows || HostPlatform.Architecture != Architecture.X64) return Array.Empty<string>();

        lock (CudaLibrariesGate)
        {
            if (_cudaLibrariesPreload != null) return _cudaLibrariesPreload;

            if (HasSystemCudaLibraries())
            {
                logger.Notification(
                    "[{0}] Using the CUDA libraries already installed on this machine; nothing to download.",
                    DiffusionPaths.ModId);
                return _cudaLibrariesPreload = Array.Empty<string>();
            }

            string directory = Path.Combine(
                DiffusionPaths.RuntimeDirectory, "cuda-libraries", CudaLibrariesVersion, CurrentRid());
            EnsurePinned(CudaLibraries, directory, () =>
            {
                LoadingNotice.Post(logger,
                    "Downloading the CUDA support libraries ({0}). This happens once.",
                    ModelAssetManager.HumanBytes(CudaLibrariesDownloadBytes));
                using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(60) };
                // These archives run to gigabytes, nearly all of it static libraries and headers, so
                // they are read entry by entry rather than downloaded and hashed whole. Every file that
                // comes out is checked against the pinned manifest, which is the same guarantee.
                CudaLibraries.Install(directory, staging =>
                {
                    foreach (NativeSource source in CudaLibrarySources())
                        NativeDownloads.ExtractFromZip(client, source, staging, logger, cancellation);
                }, "The CUDA redistributables did not contain a complete library set");
            });

            logger.Notification("[{0}] CUDA support libraries ({1}) prepared in {2}",
                DiffusionPaths.ModId, CudaLibrariesVersion, directory);
            return _cudaLibrariesPreload = CudaLibraries.PathsIn(directory);
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

    /// <summary>The four redistributable archives and the libraries each contributes.</summary>
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

    /// <summary>One redistributable archive, range-read for the named DLLs in its <c>bin/x64</c>.</summary>
    private static NativeSource CudaRedistSource(string baseUrl, string archive, string name,
                                                 params string[] fileNames) => new()
    {
        Name = name + " (NVIDIA CUDA redistributable)",
        Url = baseUrl + archive + ".zip",
        Kind = ArchiveKind.Zip,
        FileNames = fileNames,
        EntryPaths = Array.ConvertAll(fileNames, n => archive + "/bin/x64/" + n)
    };
}
