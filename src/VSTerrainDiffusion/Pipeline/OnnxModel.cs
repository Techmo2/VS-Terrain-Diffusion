using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Microsoft.ML.OnnxRuntime;
using Vintagestory.API.Common;
using VSTerrainDiffusion.Core;
using VSTerrainDiffusion.Native;

namespace VSTerrainDiffusion.Pipeline;

/// <summary>
/// A single ONNX graph plus its inference session.
///
/// Normally every model keeps its session for the life of the server, because generating one
/// terrain tile runs two or three of them and rebuilding a session for a graph of most of a
/// gigabyte costs far more than the inference does. <see cref="DiffusionConfig.OffloadModels"/>
/// trades that away on a card too small to hold all three: a session is created on demand,
/// evicting whichever model held the accelerated-provider slot before. Graphs may stay in memory
/// to make that switch cheap, or be opened from their files to save system RAM.
/// </summary>
public sealed class OnnxModel : IModelRunner
{
    private const long AutoFileMemoryThresholdBytes = 8L * 1024 * 1024 * 1024;

    private static readonly object ProviderSlotLock = new();
    private static OnnxModel _providerSlotHolder;
    private static InferenceSession _activeProviderSession;
    private static bool? _loadGraphsFromFile;

    private readonly string _name;
    private readonly ILogger _logger;
    private readonly string _graphPath;
    private readonly byte[] _graphBytes;
    private readonly long _graphSize;

    private InferenceSession _residentSession;
    private bool _disposed;
    private long _runCount;
    private long _runItems;
    private long _runStopwatchTicks;

    /// <summary>Names of this graph's inputs, in declaration order.</summary>
    private readonly List<string> _inputNames = new();
    private string[] _outputNames;

    /// <summary>
    /// Reused across runs. Both are only touched while holding this model's run lock, and building
    /// them per call means a native allocation and a managed array on every step of every tile.
    /// </summary>
    private RunOptions _runOptions;
    private readonly List<OrtValue> _inputValues = new();

    public OnnxModel(string modelFilePath, string name, ILogger logger)
    {
        _name = name;
        _logger = logger;

        var stopwatch = Stopwatch.StartNew();
        bool loadFromFile = LoadGraphsFromFile(logger);
        bool offloadProvider = ConfiguredProviderEnabled && DiffusionConfig.Instance.OffloadModels;
        _graphPath = OptimizeAtRuntime(modelFilePath, name, logger);
        _graphSize = new FileInfo(_graphPath).Length;
        _graphBytes = loadFromFile ? null : File.ReadAllBytes(_graphPath);

        // Always create one session up front: it validates the graph and, for the CPU/no-offload
        // paths, is the session used for every run.
        InferenceSession probe;
        bool providerUnavailableBeforeProbe = _providerUnavailable;
        try
        {
            probe = CreateSession(offloadProvider
                ? SessionKind.CpuOnly
                : SessionKind.Configured);
        }
        catch (Exception e) when (!SamePath(_graphPath, modelFilePath))
        {
            logger.Warning(
                "[{0}] Cached optimised graph for '{1}' could not be loaded ({2}); rebuilding it",
                DiffusionPaths.ModId, name, e.Message);
            _providerUnavailable = providerUnavailableBeforeProbe;
            _graphPath = OptimizeAtRuntime(modelFilePath, name, logger, rebuild: true);
            _graphSize = new FileInfo(_graphPath).Length;
            _graphBytes = loadFromFile ? null : File.ReadAllBytes(_graphPath);
            probe = CreateSession(offloadProvider
                ? SessionKind.CpuOnly
                : SessionKind.Configured);
        }

        foreach (string inputName in probe.InputNames) _inputNames.Add(inputName);
        _outputNames = probe.OutputNames.Count > 0 ? new[] { probe.OutputNames[0] } : Array.Empty<string>();

        if (offloadProvider)
        {
            // Only the metadata was needed; accelerated sessions are created per slot claim.
            probe.Dispose();
            logger.Notification("[{0}] Model '{1}' prepared ({2}) in {3} ms",
                DiffusionPaths.ModId, name, ModelAssetManager.HumanBytes(_graphSize), stopwatch.ElapsedMilliseconds);
        }
        else
        {
            _residentSession = probe;
            logger.Notification("[{0}] Model '{1}' loaded on {2} ({3}) in {4} ms",
                DiffusionPaths.ModId, name, UsesConfiguredProvider ? ActiveProvider : InferenceProvider.Cpu,
                ModelAssetManager.HumanBytes(_graphSize), stopwatch.ElapsedMilliseconds);
        }
    }

    /// <summary>
    /// Set the first time an accelerated session cannot be created (a mismatched runtime, missing
    /// device or exhausted memory). Everything falls back to CPU rather than failing world generation.
    /// </summary>
    private static volatile bool _providerUnavailable;

    private static bool ConfiguredProviderEnabled =>
        OnnxRuntimeBootstrap.Provider is not (InferenceProvider.Cpu or InferenceProvider.OpenVino) &&
        !_providerUnavailable;

    private bool UsesConfiguredProvider => ConfiguredProviderEnabled;

    private static bool LoadGraphsFromFile(ILogger logger)
    {
        if (_loadGraphsFromFile.HasValue) return _loadGraphsFromFile.Value;

        string mode = DiffusionConfig.Instance.ModelLoadMode;
        bool fromFile;
        string reason;

        if (mode == "file")
        {
            fromFile = true;
            reason = "configured";
        }
        else if (mode == "memory")
        {
            fromFile = false;
            reason = "configured";
        }
        else if (!ConfiguredProviderEnabled)
        {
            fromFile = true;
            reason = "auto, CPU provider";
        }
        else if (!DiffusionConfig.Instance.OffloadModels)
        {
            fromFile = true;
            reason = "auto, sessions stay resident";
        }
        else
        {
            long availableMemory = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
            fromFile = availableMemory > 0 && availableMemory < AutoFileMemoryThresholdBytes;
            reason = fromFile
                ? $"auto, {ModelAssetManager.HumanBytes(availableMemory)} available"
                : availableMemory > 0
                    ? $"auto, provider model switching and {ModelAssetManager.HumanBytes(availableMemory)} available"
                    : "auto, provider model switching";
        }

        _loadGraphsFromFile = fromFile;
        logger.Notification("[{0}] Model graph loading: {1} ({2})", DiffusionPaths.ModId,
            fromFile ? "file" : "memory", reason);
        return fromFile;
    }

    internal static void ResetSharedState()
    {
        lock (ProviderSlotLock)
        {
            _activeProviderSession?.Dispose();
            _activeProviderSession = null;
            _providerSlotHolder = null;
            _providerUnavailable = false;
            _loadGraphsFromFile = null;
        }
    }

    /// <summary>The provider actually in use, which is CPU if the requested provider was unusable.</summary>
    public static InferenceProvider ActiveProvider =>
        _providerUnavailable ? InferenceProvider.Cpu : OnnxRuntimeBootstrap.Provider;

    public string Backend => UsesConfiguredProvider
        ? $"ONNX Runtime {ActiveProvider}"
        : "ONNX Runtime CPU";

    /// <summary>Number of completed inference calls since this model was loaded.</summary>
    public long RunCount => Interlocked.Read(ref _runCount);

    /// <summary>Wall-clock milliseconds spent inside ONNX Runtime inference calls.</summary>
    public long RunMilliseconds =>
        Interlocked.Read(ref _runStopwatchTicks) * 1000 / Stopwatch.Frequency;

    /// <summary>Total batch items processed by completed inference calls.</summary>
    public long RunItems => Interlocked.Read(ref _runItems);

    private enum SessionKind
    {
        /// <summary>Use whatever execution provider was resolved at startup.</summary>
        Configured,

        /// <summary>Plain CPU session, used only to read graph metadata cheaply.</summary>
        CpuOnly
    }

    private InferenceSession CreateSession(SessionKind kind)
    {
        if (kind == SessionKind.Configured && UsesConfiguredProvider)
        {
            try
            {
                return CreateSessionCore(useConfiguredProvider: true);
            }
            catch (Exception e)
            {
                _providerUnavailable = true;
                _logger.Warning(
                    "[{0}] The {1} execution provider could not be initialised, so terrain generation will run on the CPU " +
                    "(much slower). Cause: {2}",
                    DiffusionPaths.ModId, OnnxRuntimeBootstrap.Provider, e.Message);
            }
        }

        return CreateSessionCore(useConfiguredProvider: false);
    }

    private InferenceSession CreateSessionCore(bool useConfiguredProvider)
    {
        var options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR
        };

        if (useConfiguredProvider)
        {
            switch (OnnxRuntimeBootstrap.Provider)
            {
                case InferenceProvider.Cuda:
                    using (var cuda = new OrtCUDAProviderOptions())
                    {
                        cuda.UpdateOptions(new Dictionary<string, string>
                        {
                            // Grow the arena only by what is requested; never pre-allocate all VRAM.
                            { "arena_extend_strategy", "kSameAsRequested" },
                            // Heuristic search starts fast and keeps cuDNN workspaces small. Both
                            // of the obvious alternatives were measured and rejected: EXHAUSTIVE
                            // with cudnn_conv_use_max_workspace bought about 2% overall and then
                            // asked for a 5 GB workspace for one decoder convolution, which fails
                            // outright on a 6 GB card; and turning off use_tf32 (on by default, and
                            // worth roughly a factor of two here) sends the same convolution down
                            // an algorithm that wants the same 5 GB. The default is the fast path.
                            { "cudnn_conv_algo_search", "HEURISTIC" },
                            { "do_copy_in_default_stream", "1" }
                        });
                        options.AppendExecutionProvider_CUDA(cuda);
                    }
                    break;

                case InferenceProvider.DirectMl:
                    // DirectML requires sequential execution and no memory-pattern reuse.
                    options.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL;
                    options.EnableMemoryPattern = false;
                    options.AppendExecutionProvider_DML(0);
                    break;

                case InferenceProvider.CoreMl:
                    // Subgraph mode lets CoreML take what it can and leaves the rest on CPU.
                    options.AppendExecutionProvider_CoreML(CoreMLFlags.COREML_FLAG_ENABLE_ON_SUBGRAPH);
                    break;

            }
        }

        try
        {
            return _graphBytes == null
                ? new InferenceSession(_graphPath, options)
                : new InferenceSession(_graphBytes, options);
        }
        finally
        {
            options.Dispose();
        }
    }

    /// <summary>
    /// Runs the graph. <paramref name="inputs"/> must supply one entry per graph input, in the
    /// order the graph declares them.
    /// </summary>
    public float[] Run(IReadOnlyList<(float[] Data, long[] Shape)> inputs)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(OnnxModel));

        InferenceSession resident = _residentSession;
        if (resident != null)
        {
            lock (resident) return RunWithSession(resident, inputs);
        }

        lock (ProviderSlotLock)
        {
            ClaimProviderSlot();

            // ClaimProviderSlot promotes the session to _residentSession if the provider turned
            // out to be unusable, in which case there is nothing in the shared slot to run.
            resident = _residentSession;
            if (resident != null) return RunWithSession(resident, inputs);

            return RunWithSession(_activeProviderSession, inputs);
        }
    }

    /// <summary>Convenience wrapper for the pipeline's (x, noise_labels, cond_0..cond_n) signature.</summary>
    public float[] RunModel(float[] x, long[] xShape, float[] noiseLabels,
                            float[][] condInputs, long[][] condShapes)
    {
        int condCount = condInputs?.Length ?? 0;
        var inputs = new List<(float[], long[])>(2 + condCount)
        {
            (x, xShape),
            (noiseLabels, new long[] { noiseLabels.Length })
        };
        for (int i = 0; i < condCount; i++) inputs.Add((condInputs[i], condShapes[i]));
        return Run(inputs);
    }

    private float[] RunWithSession(InferenceSession session, IReadOnlyList<(float[] Data, long[] Shape)> inputs)
    {
        if (inputs.Count != _inputNames.Count)
        {
            throw new ArgumentException(
                $"Model '{_name}' expects {_inputNames.Count} inputs ({string.Join(", ", _inputNames)}) but got {inputs.Count}");
        }

        // CreateTensorValueFromMemory pins the caller's array rather than copying it, so the only
        // copy on the way in is the one the execution provider makes onto the device.
        List<OrtValue> values = _inputValues;
        values.Clear();
        try
        {
            foreach ((float[] data, long[] shape) in inputs)
            {
                values.Add(OrtValue.CreateTensorValueFromMemory(data, shape));
            }

            _runOptions ??= new RunOptions();
            long started = Stopwatch.GetTimestamp();
            using IDisposableReadOnlyCollection<OrtValue> results =
                session.Run(_runOptions, _inputNames, values, _outputNames);
            long elapsed = Stopwatch.GetTimestamp() - started;

            Interlocked.Add(ref _runStopwatchTicks, elapsed);
            Interlocked.Increment(ref _runCount);
            Interlocked.Add(ref _runItems, inputs.Count > 0 ? inputs[0].Shape[0] : 0);

            return results[0].GetTensorDataAsSpan<float>().ToArray();
        }
        finally
        {
            foreach (OrtValue value in values) value.Dispose();
            values.Clear();
        }
    }

    /// <summary>
    /// Makes this model the active accelerated one, disposing whichever session held the slot.
    /// Must be called under <see cref="ProviderSlotLock"/>.
    /// </summary>
    private void ClaimProviderSlot()
    {
        if (_providerSlotHolder == this && _activeProviderSession != null) return;

        if (_activeProviderSession != null)
        {
            _activeProviderSession.Dispose();
            _activeProviderSession = null;
            _providerSlotHolder = null;
        }

        InferenceSession session = CreateSession(SessionKind.Configured);

        if (_providerUnavailable)
        {
            // The configured provider failed and we got a CPU session instead. Keep it resident:
            // there is no reason to tear it down and rebuild it every time another model runs.
            _residentSession = session;
            return;
        }

        _activeProviderSession = session;
        _providerSlotHolder = this;
    }

    /// <summary>
    /// Runs the graph optimiser once and caches the result on disk, so later starts skip the
    /// (slow) constant folding and fusion passes. Falls back to the raw graph on any failure.
    /// </summary>
    private static string OptimizeAtRuntime(string sourcePath, string name, ILogger logger,
                                            bool rebuild = false)
    {
        string cachePath = ResolveOptimizedPath(sourcePath, name);
        try
        {
            if (File.Exists(cachePath))
            {
                if (!rebuild) return cachePath;
                File.Delete(cachePath);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
            string tempPath = cachePath + ".tmp";
            if (File.Exists(tempPath)) File.Delete(tempPath);

            using (var options = new SessionOptions
                   {
                       GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_EXTENDED,
                       OptimizedModelFilePath = tempPath,
                       LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR
                   })
            using (var _ = new InferenceSession(sourcePath, options))
            {
                // Creating the session writes the optimised graph to disk.
            }

            long sourceSize = new FileInfo(sourcePath).Length;
            long optimizedSize = new FileInfo(tempPath).Length;
            File.Move(tempPath, cachePath, overwrite: true);
            logger.Notification("[{0}] Optimised '{1}' ({2} -> {3})", DiffusionPaths.ModId, name,
                ModelAssetManager.HumanBytes(sourceSize), ModelAssetManager.HumanBytes(optimizedSize));
            return cachePath;
        }
        catch (Exception e)
        {
            logger.Warning("[{0}] Graph optimisation failed for '{1}', using the unoptimised model: {2}",
                DiffusionPaths.ModId, name, e.Message);
            return sourcePath;
        }
    }

    private static bool SamePath(string left, string right) => string.Equals(
        Path.GetFullPath(left), Path.GetFullPath(right),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static string ResolveOptimizedPath(string sourcePath, string name)
    {
        string hash = ModelAssetManager.Sha256Hex(sourcePath)[..16];
        InferenceProvider provider = OnnxRuntimeBootstrap.Provider == InferenceProvider.OpenVino
            ? InferenceProvider.Cpu
            : OnnxRuntimeBootstrap.Provider;
        string fileName = $"{name}-{OnnxRuntimeBootstrap.ActiveRuntimeVersion}-{provider}-{hash}.onnx"
            .ToLowerInvariant();
        return Path.Combine(DiffusionPaths.OptimizedModelDirectory, fileName);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        lock (ProviderSlotLock)
        {
            if (_providerSlotHolder == this && _activeProviderSession != null)
            {
                _activeProviderSession.Dispose();
                _activeProviderSession = null;
                _providerSlotHolder = null;
            }
        }

        _residentSession?.Dispose();
        _residentSession = null;
        _runOptions?.Dispose();
        _runOptions = null;
    }
}
