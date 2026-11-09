using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Vintagestory.API.Common;
using VSTerrainDiffusion.Core;

namespace VSTerrainDiffusion.Native;

/// <summary>
/// Locates (and, if needed, downloads) the native ONNX Runtime that matches this machine and the
/// world's execution provider, and everything that provider needs loaded beside it. Nothing here
/// is loaded into the game: the result is a <see cref="RuntimePlan"/> the
/// <see cref="InferenceWorker"/> loads, so each world gets the runtime it asks for.
/// </summary>
/// <remarks>
/// <para>The mod ships only the ~1 MB managed binding; native libraries are pulled from pinned
/// official packages on first use. ZIP packages are range-read so only the required entries are
/// transferred.</para>
/// <para>Split by runtime across partial files: this one plans a world's runtime; <c>.OnnxRuntime</c>
/// fetches ONNX Runtime itself; <c>.OpenVino</c>, <c>.TensorRtRtx</c>, <c>.Cuda</c> and
/// <c>.VisualCpp</c> fetch what each provider needs beside it, as <see cref="PinnedFileSet"/>s.</para>
/// </remarks>
public static partial class OnnxRuntimeBootstrap
{
    /// <summary>ONNX Runtime version. Must match the Microsoft.ML.OnnxRuntime.Managed package reference.</summary>
    public const string OnnxRuntimeVersion = "1.24.4";

    /// <summary>Marker file name every pinned add-on runtime writes beside its files.</summary>
    private const string VerificationFileName = ".sources.sha256";

    /// <summary>The ONNX Runtime directory of the current world's runtime.</summary>
    private static string _nativeDirectory;

    /// <summary>The provider the current world's worker was started for.</summary>
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
        InferenceProvider.Rocm =>
            $"MIGraphX on AMD's ONNX Runtime {OnnxRuntimeVersion}",
        _ => $"ONNX Runtime {ActiveRuntimeVersion}"
    };

    /// <summary>Where MIGraphX keeps the programs it compiles for this machine's GPU.</summary>
    public static string RocmCacheDirectory =>
        Path.Combine(DiffusionPaths.OptimizedModelDirectory, "migraphx", OnnxRuntimeVersion);

    /// <summary>Where TensorRT RTX keeps the engines it builds for this machine.</summary>
    public static string TensorRtRtxCacheDirectory =>
        Path.Combine(DiffusionPaths.OptimizedModelDirectory, "tensorrt-rtx", TensorRtRtxVersion);

    /// <summary>The world setting code of the inference device.</summary>
    private static string DeviceCode => InferenceSettings.Code(nameof(InferenceSettings.InferenceDevice));

    /// <summary>The world setting code that allows runtime downloads.</summary>
    private static string DownloadRuntimeCode => InferenceSettings.Code(nameof(InferenceSettings.DownloadRuntime));

    /// <summary>
    /// Works out, and downloads if needed, the runtime for this world's device. Blocking; may
    /// download tens of megabytes (or a few hundred for CUDA). A compatible device whose files
    /// cannot be prepared - a failed download, a missing file - gives way to
    /// <see cref="FallbackFor"/> for this world only.
    /// </summary>
    internal static RuntimePlan Prepare(ILogger logger, CancellationToken cancellation = default)
    {
        Downloaded = false;
        InferenceProvider provider = ResolveRequestedProvider(InferenceSettings.Current.InferenceDevice, logger);
        try
        {
            return Prepare(provider, logger, cancellation);
        }
        catch (Exception e) when (provider != InferenceProvider.Cpu && e is not OperationCanceledException)
        {
            InferenceProvider fallback = FallbackFor(provider, logger);
            WarnFallback(provider, fallback, e, logger);
            return Prepare(fallback, logger, cancellation);
        }
    }

    /// <summary>The runtime for <paramref name="provider"/>, downloading what is missing.</summary>
    internal static RuntimePlan Prepare(InferenceProvider provider, ILogger logger, CancellationToken cancellation)
    {
        // First in the list, so the worker has it before anything that imports it.
        var preload = new List<string>(PrepareVisualCppRuntime(logger, cancellation));

        // OpenVINO runs beside an ORT CPU build; TensorRT RTX is a plugin on the CUDA build.
        string directory = EnsureNativeFiles(
            OnnxProviderFor(provider), provider == InferenceProvider.Cuda, logger, cancellation);
        var plan = new RuntimePlan { Provider = provider, OnnxRuntimeDirectory = directory };

        if (provider == InferenceProvider.OpenVino) PrepareOpenVino(logger, cancellation);
        if (provider == InferenceProvider.Cuda) preload.AddRange(PrepareCudaLibraries(logger, cancellation));
        if (provider == InferenceProvider.TensorRtRtx)
        {
            string tensorRt = PrepareTensorRtRtx(logger, cancellation);
            foreach (string dependency in TensorRtRtxDependencies) preload.Add(Path.Combine(tensorRt, dependency));
            plan.PluginName = TensorRtRtxEpName;
            plan.PluginLibrary = Path.Combine(tensorRt, TensorRtRtxProviderLibrary);
        }

        plan.Preload = preload.ToArray();
        return plan;
    }

    /// <summary>Records the plan the current world's worker runs, for the status command.</summary>
    internal static void Use(RuntimePlan plan)
    {
        Provider = plan.Provider;
        _nativeDirectory = plan.OnnxRuntimeDirectory;
    }

    /// <summary>
    /// Says that this world runs on <paramref name="fallback"/>. The machine can run the chosen
    /// provider (<see cref="InferenceCompatibility"/> let it through), so this was a failure of the
    /// moment; the world setting is left alone, and the next world tries it again.
    /// </summary>
    internal static void WarnFallback(InferenceProvider provider, InferenceProvider fallback, Exception cause,
                                      ILogger logger)
    {
        logger.Warning("[{0}] Could not prepare the {1} runtime: {2}", DiffusionPaths.ModId, provider, cause.Message);
        logger.Warning("[{0}] This world runs on {1} instead; {2} stays selected in {3}. Provider changes " +
                       "can alter newly generated terrain slightly.",
            DiffusionPaths.ModId, DeviceName(fallback), DeviceName(provider), DeviceCode);
    }

    /// <summary>
    /// Where a provider goes when its runtime cannot be prepared: whatever this machine would have
    /// picked for itself (<see cref="InferenceCompatibility.AutomaticDevice"/>), or the CPU when that
    /// is the provider that just failed. Only this world moves; the world setting is not touched.
    /// </summary>
    /// <remarks>
    /// Naming a provider here instead would assume hardware the player may not have - a fallback
    /// fixed at CUDA lands an AMD machine on a provider it cannot run at all.
    /// </remarks>
    internal static InferenceProvider FallbackFor(InferenceProvider provider, ILogger logger)
    {
        // OpenVINO is already an ONNX Runtime CPU session everywhere except the decoder, so the
        // CPU is where it degrades to - not a GPU the player did not ask this world to be built on.
        if (provider == InferenceProvider.OpenVino) return InferenceProvider.Cpu;

        InferenceProvider automatic = ResolveRequestedProvider("auto", logger);
        return automatic == provider ? InferenceProvider.Cpu : automatic;
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

    /// <summary>The provider's spelling in the <c>terraindiffusionInferenceDevice</c> world setting.</summary>
    private static string DeviceName(InferenceProvider provider) => provider switch
    {
        InferenceProvider.Cuda => "cuda",
        InferenceProvider.DirectMl => "directml",
        InferenceProvider.CoreMl => "coreml",
        InferenceProvider.OpenVino => "openvino",
        InferenceProvider.TensorRtRtx => "tensorrt-rtx",
        InferenceProvider.Rocm => "rocm",
        _ => "cpu"
    };

    /// <summary>
    /// The provider a device setting names, "auto" resolved to this machine's choice. Refuses a device
    /// this machine cannot run, so no path reaches a native runtime it cannot load.
    /// </summary>
    private static InferenceProvider ResolveRequestedProvider(string configured, ILogger logger)
    {
        InferenceCompatibility compatibility = InferenceCompatibility.Current;
        // The world was refused as it loaded if this machine cannot run the device. Checked again
        // here so that no path can reach a native runtime the machine cannot run.
        compatibility.RequireDevice(configured, logger);

        string device = configured == "auto" ? compatibility.AutomaticDevice() : configured;

        return device switch
        {
            "cuda" => InferenceProvider.Cuda,
            "directml" => InferenceProvider.DirectMl,
            "coreml" => InferenceProvider.CoreMl,
            "openvino" => InferenceProvider.OpenVino,
            "tensorrt-rtx" => InferenceProvider.TensorRtRtx,
            "rocm" => InferenceProvider.Rocm,
            _ => InferenceProvider.Cpu
        };
    }

    /// <summary>
    /// Makes sure a pinned add-on runtime is installed in <paramref name="directory"/> and verified,
    /// downloading it when allowed. With downloads off, files without a marker are accepted as an
    /// administrator's own install; an auto-downloaded install, identified by its marker, must match
    /// the pinned sources rather than silently accepting a stale or interrupted one.
    /// </summary>
    /// <param name="files">The pinned set.</param>
    /// <param name="directory">Where it lives.</param>
    /// <param name="install">Downloads and installs the set; called only when allowed and needed.</param>
    /// <exception cref="InvalidOperationException">Downloads are off and no acceptable install was found.</exception>
    /// <exception cref="FileNotFoundException">The set is still incomplete afterwards.</exception>
    private static void EnsurePinned(PinnedFileSet files, string directory, Action install)
    {
        bool filesPresent = files.IsPresent(directory);
        if (!filesPresent || !files.IsVerified(directory))
        {
            if (!InferenceSettings.Current.DownloadRuntime)
            {
                if (!filesPresent || files.HasMarker(directory))
                {
                    throw new InvalidOperationException(
                        $"Runtime downloads are disabled and no verified copy of the {files.Name} was found in " + directory);
                }
            }
            else
            {
                Downloaded = true;
                install();
            }
        }

        if (!files.IsPresent(directory))
            throw new FileNotFoundException($"Not every file of the {files.Name} is in " + directory);
    }
}
