using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
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
    CoreMl
}

/// <summary>
/// Locates (and, if needed, downloads) the native ONNX Runtime that matches this machine and the
/// configured execution provider, then teaches the runtime's P/Invoke layer where to find it.
///
/// The mod ships only the ~1 MB managed binding; the native library is pulled from the pinned NuGet
/// packages on first use, using range requests so only the files actually needed are transferred.
/// </summary>
public static class OnnxRuntimeBootstrap
{
    /// <summary>ONNX Runtime version. Must match the Microsoft.ML.OnnxRuntime.Managed package reference.</summary>
    public const string OnnxRuntimeVersion = "1.24.4";

    private const string DirectMlVersion = "1.15.4";

    private static readonly object Gate = new();
    private static bool _initialised;
    private static string _nativeDirectory;

    /// <summary>The provider that was actually resolved, available after <see cref="Initialize"/>.</summary>
    public static InferenceProvider Provider { get; private set; } = InferenceProvider.Cpu;

    /// <summary>Directory containing the resolved native libraries.</summary>
    public static string NativeDirectory => _nativeDirectory;

    /// <summary>
    /// Resolves the native runtime and installs the DllImport resolver. Blocking; may download
    /// tens of megabytes (or a few hundred for CUDA). Safe to call more than once.
    /// </summary>
    public static void Initialize(ILogger logger, CancellationToken cancellation = default)
    {
        if (_initialised) return;
        lock (Gate)
        {
            if (_initialised) return;

            InferenceProvider requested = ResolveRequestedProvider(DiffusionConfig.Instance.InferenceDevice, logger);
            InferenceProvider provider = requested;
            string directory;

            try
            {
                directory = EnsureNativeFiles(provider, logger, cancellation);
            }
            catch (Exception e) when (provider != InferenceProvider.Cpu)
            {
                logger.Warning("[{0}] Could not prepare the {1} runtime ({2}); falling back to CPU.",
                    DiffusionPaths.ModId, provider, e.Message);
                provider = InferenceProvider.Cpu;
                directory = EnsureNativeFiles(provider, logger, cancellation);
            }

            _nativeDirectory = directory;
            Provider = provider;
            InstallResolver(directory, logger);
            _initialised = true;

            logger.Notification("[{0}] ONNX Runtime {1} ({2}) loaded from {3}",
                DiffusionPaths.ModId, OnnxRuntimeVersion, provider, directory);
        }
    }

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

        /// <summary>Exact archive paths for <see cref="ArchiveKind.Zip"/>; ignored for tarballs.</summary>
        public string[] EntryPaths;

        /// <summary>File names to pull out of a tarball.</summary>
        public string[] FileNames;
    }

    private static string EnsureNativeFiles(InferenceProvider provider, ILogger logger, CancellationToken cancellation)
    {
        string rid = CurrentRid();
        string flavour = provider == InferenceProvider.Cuda
            ? "cuda" + DetectCudaMajorVersion()
            : provider.ToString().ToLowerInvariant();
        string directory = Path.Combine(DiffusionPaths.RuntimeDirectory, OnnxRuntimeVersion, flavour, rid);

        // A directory that already holds a runtime (downloaded earlier, or supplied by hand) is
        // used as-is and never overwritten.
        if (HasPrimaryLibrary(directory)) return directory;

        if (!DiffusionConfig.Instance.DownloadRuntime)
        {
            throw new InvalidOperationException(
                "Runtime downloads are disabled (downloadRuntime=false) and no ONNX Runtime was found in " + directory);
        }

        List<NativeSource> sources = SourcesFor(provider, rid);
        Directory.CreateDirectory(directory);

        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        foreach (NativeSource source in sources)
        {
            if (source.Kind == ArchiveKind.TarGz) ExtractFromTarGz(client, source, directory, logger, cancellation);
            else ExtractFromZip(client, source, directory, logger, cancellation);
        }

        if (!HasPrimaryLibrary(directory))
        {
            throw new FileNotFoundException("ONNX Runtime native library missing after download in " + directory);
        }
        return directory;
    }

    private static void ExtractFromZip(HttpClient client, NativeSource source, string directory,
                                       ILogger logger, CancellationToken cancellation)
    {
        List<RemoteZipExtractor.Entry> entries = RemoteZipExtractor.ReadCentralDirectory(client, source.Url, cancellation);

        foreach (string wanted in source.EntryPaths)
        {
            RemoteZipExtractor.Entry entry = entries.Find(e =>
                string.Equals(e.Name, wanted, StringComparison.OrdinalIgnoreCase));
            if (entry == null)
            {
                throw new FileNotFoundException($"{source.Name} does not contain {wanted}");
            }

            string target = Path.Combine(directory, Path.GetFileName(entry.Name));
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

        List<string> written = RemoteTarGzExtractor.Extract(client, source.Url, source.FileNames, directory, cancellation);
        foreach (string name in source.FileNames)
        {
            if (!written.Contains(name) && !File.Exists(Path.Combine(directory, name)))
            {
                throw new FileNotFoundException($"{source.Name} does not contain {name}");
            }
        }
    }

    private static string NuGetUrl(string packageId, string version)
    {
        string id = packageId.ToLowerInvariant();
        return $"https://api.nuget.org/v3-flatcontainer/{id}/{version}/{id}.{version}.nupkg";
    }

    private static string GitHubAssetUrl(string assetName) =>
        $"https://github.com/microsoft/onnxruntime/releases/download/v{OnnxRuntimeVersion}/{assetName}";

    private static List<NativeSource> SourcesFor(InferenceProvider provider, string rid)
    {
        var sources = new List<NativeSource>();
        switch (provider)
        {
            case InferenceProvider.Cuda when rid is "linux-x64" or "win-x64":
            {
                int cudaMajor = DetectCudaMajorVersion();
                bool windows = rid == "win-x64";

                if (cudaMajor >= 13)
                {
                    // NuGet only ships the CUDA 12 build, so the CUDA 13 flavour comes from the
                    // GitHub release assets instead.
                    string platform = windows ? "win-x64" : "linux-x64";
                    string extension = windows ? "zip" : "tgz";
                    string asset = $"onnxruntime-{platform}-gpu_cuda13-{OnnxRuntimeVersion}.{extension}";
                    string root = $"onnxruntime-{platform}-gpu_cuda13-{OnnxRuntimeVersion}";

                    string[] fileNames = windows
                        ? new[] { "onnxruntime.dll", "onnxruntime_providers_shared.dll", "onnxruntime_providers_cuda.dll" }
                        : new[] { "libonnxruntime.so", "libonnxruntime_providers_shared.so", "libonnxruntime_providers_cuda.so" };

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
                    string[] fileNames = windows
                        ? new[] { "onnxruntime.dll", "onnxruntime_providers_shared.dll", "onnxruntime_providers_cuda.dll" }
                        : new[] { "libonnxruntime.so", "libonnxruntime_providers_shared.so", "libonnxruntime_providers_cuda.so" };

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

    private static int _cudaMajor = -1;

    /// <summary>
    /// Which CUDA runtime is installed, since ONNX Runtime ships separate builds for CUDA 12 and 13
    /// and loading the wrong one fails with a missing libcublasLt. Returns 12 when nothing is found,
    /// which is the more common configuration.
    /// </summary>
    internal static int DetectCudaMajorVersion()
    {
        if (_cudaMajor > 0) return _cudaMajor;

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

    private static bool HasPrimaryLibrary(string directory)
        => Directory.Exists(directory) && File.Exists(Path.Combine(directory, PrimaryLibraryName()));

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

    private static void InstallResolver(string directory, ILogger logger)
    {
        Assembly onnxAssembly;
        try
        {
            onnxAssembly = Assembly.Load("Microsoft.ML.OnnxRuntime");
        }
        catch (Exception e)
        {
            throw new InvalidOperationException(
                "Microsoft.ML.OnnxRuntime.dll is missing from the mod folder. Reinstall the mod archive.", e);
        }

        NativeLibrary.SetDllImportResolver(onnxAssembly, (libraryName, assembly, searchPath) =>
        {
            if (!libraryName.Contains("onnxruntime", StringComparison.OrdinalIgnoreCase)) return IntPtr.Zero;

            foreach (string candidate in CandidateFileNames(libraryName))
            {
                string path = Path.Combine(directory, candidate);
                if (File.Exists(path) && NativeLibrary.TryLoad(path, out IntPtr handle)) return handle;
            }

            logger.Warning("[{0}] Could not resolve native library '{1}' in {2}", DiffusionPaths.ModId, libraryName, directory);
            return IntPtr.Zero;
        });
    }

    private static IEnumerable<string> CandidateFileNames(string libraryName)
    {
        yield return libraryName;
        if (OperatingSystem.IsWindows())
        {
            yield return libraryName + ".dll";
        }
        else if (OperatingSystem.IsMacOS())
        {
            yield return "lib" + libraryName + ".dylib";
            yield return libraryName + ".dylib";
        }
        else
        {
            yield return "lib" + libraryName + ".so";
            yield return libraryName + ".so";
        }
    }
}
