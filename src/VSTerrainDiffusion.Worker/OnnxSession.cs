using System.Diagnostics;
using Microsoft.ML.OnnxRuntime;

namespace VSTerrainDiffusion.Native;

/// <summary>
/// A single ONNX graph plus its inference session, inside the worker.
///
/// Normally every model keeps its session for the life of the worker, because generating one
/// terrain tile runs two or three of them and rebuilding a session for a graph of most of a
/// gigabyte costs far more than the inference does. <see cref="ModelSpec.Offload"/> trades that
/// away on a card too small to hold all three: a session is created on demand, evicting whichever
/// model held the accelerated-provider slot before. Graphs may stay in memory to make that switch
/// cheap, or be opened from their files to save system RAM.
/// </summary>
internal sealed class OnnxSession : IDisposable
{
    private static readonly object ProviderSlotLock = new();
    private static OnnxSession? _providerSlotHolder;
    private static InferenceSession? _activeProviderSession;

    /// <summary>
    /// Set the first time an accelerated session cannot be created (a mismatched runtime, missing
    /// device or exhausted memory). Everything falls back to CPU rather than failing world generation.
    /// </summary>
    private static volatile bool _providerUnavailable;

    /// <summary>The provider sessions are created with, from the game's plan.</summary>
    internal static InferenceProvider Provider { get; set; } = InferenceProvider.Cpu;

    internal static string? TensorRtRtxEpName { get; set; }

    private readonly ModelSpec _spec;
    private readonly string _graphPath;
    private byte[]? _graphBytes;
    private readonly long _graphSize;

    private InferenceSession? _residentSession;
    private bool _disposed;

    /// <summary>Names of this graph's inputs, in declaration order.</summary>
    private readonly List<string> _inputNames = new();
    private readonly string[] _outputNames;

    /// <summary>Reused across runs, which the worker serialises per model.</summary>
    private readonly RunOptions _runOptions = new();
    private readonly List<OrtValue> _inputValues = new();

    internal OnnxSession(ModelSpec spec)
    {
        _spec = spec;

        var stopwatch = Stopwatch.StartNew();
        bool offloadProvider = ConfiguredProviderEnabled && !spec.CpuOnly && spec.Offload;
        _graphPath = spec.OptimizedPath == null
            ? spec.SourcePath
            : OptimizeAtRuntime(spec.SourcePath, spec.OptimizedPath, spec.Name);
        _graphSize = new FileInfo(_graphPath).Length;
        _graphBytes = spec.LoadFromFile ? null : File.ReadAllBytes(_graphPath);

        // Always create one session up front: it validates the graph and, for the CPU/no-offload
        // paths, is the session used for every run.
        InferenceSession probe;
        bool providerUnavailableBeforeProbe = _providerUnavailable;
        try
        {
            probe = CreateSession(configured: !offloadProvider);
        }
        catch (Exception e) when (!SamePath(_graphPath, spec.SourcePath))
        {
            WorkerLog.Warning($"Cached optimised graph for '{spec.Name}' could not be loaded ({e.Message}); rebuilding it");
            _providerUnavailable = providerUnavailableBeforeProbe;
            _graphPath = OptimizeAtRuntime(spec.SourcePath, spec.OptimizedPath!, spec.Name, rebuild: true);
            _graphSize = new FileInfo(_graphPath).Length;
            _graphBytes = spec.LoadFromFile ? null : File.ReadAllBytes(_graphPath);
            probe = CreateSession(configured: !offloadProvider);
        }

        foreach (string inputName in probe.InputNames) _inputNames.Add(inputName);
        _outputNames = probe.OutputNames.Count > 0 ? new[] { probe.OutputNames[0] } : Array.Empty<string>();

        if (offloadProvider)
        {
            // Only the metadata was needed; accelerated sessions are created per slot claim.
            probe.Dispose();
            WorkerLog.Notification($"Model '{spec.Name}' prepared ({Bytes(_graphSize)}) in {stopwatch.ElapsedMilliseconds} ms");
        }
        else
        {
            _residentSession = probe;

            // This session lasts as long as the model does, so nothing will ask for the graph again.
            _graphBytes = null;

            WorkerLog.Notification($"Model '{spec.Name}' loaded on {(UsesConfiguredProvider ? ActiveProvider : InferenceProvider.Cpu)} " +
                                   $"({Bytes(_graphSize)}) in {stopwatch.ElapsedMilliseconds} ms");
        }
    }

    private static bool ConfiguredProviderEnabled =>
        Provider is not (InferenceProvider.Cpu or InferenceProvider.OpenVino) && !_providerUnavailable;

    private bool UsesConfiguredProvider => ConfiguredProviderEnabled && !_spec.CpuOnly;

    /// <summary>The provider actually in use, which is CPU if the requested provider was unusable.</summary>
    internal static InferenceProvider ActiveProvider => _providerUnavailable ? InferenceProvider.Cpu : Provider;

    internal ModelInfo Info => new()
    {
        Backend = UsesConfiguredProvider ? $"ONNX Runtime {ActiveProvider}" : "ONNX Runtime CPU",
        ActiveProvider = UsesConfiguredProvider ? ActiveProvider : InferenceProvider.Cpu
    };

    private InferenceSession CreateSession(bool configured)
    {
        if (configured && UsesConfiguredProvider)
        {
            try
            {
                return CreateSessionCore(useConfiguredProvider: true);
            }
            catch (Exception e)
            {
                _providerUnavailable = true;
                WorkerLog.Warning(
                    $"The {Provider} execution provider could not be initialised, so terrain generation will run on the CPU " +
                    $"(much slower). This provider change can alter newly generated terrain slightly. Cause: {e.Message}");
            }
        }

        return CreateSessionCore(useConfiguredProvider: false);
    }

    /// <summary>
    /// Selects the TensorRT RTX plugin provider, which is chosen by device rather than by name and
    /// needs a shape profile before it will build an engine.
    /// </summary>
    private void AppendTensorRtRtx(SessionOptions options)
    {
        string cache = _spec.TensorRtRtxCacheDirectory
                       ?? throw new InvalidOperationException("No TensorRT RTX engine cache directory");
        Directory.CreateDirectory(cache);
        var providerOptions = new Dictionary<string, string>
        {
            // Engines are built for this GPU and driver, then reused.
            { "nv_runtime_cache_path", cache },
            // Measured the same as the unbounded default; kept as a bound, since this mod has form
            // for workspace requests a 6 GB card cannot satisfy (see the cuDNN options below).
            { "nv_max_workspace_size", (256L * 1024 * 1024).ToString() }
        };

        if (_spec.TensorRtRtxProfileMin != null && _spec.TensorRtRtxProfileMax != null)
        {
            providerOptions["nv_profile_min_shapes"] = _spec.TensorRtRtxProfileMin;
            providerOptions["nv_profile_opt_shapes"] = _spec.TensorRtRtxProfileMax;
            providerOptions["nv_profile_max_shapes"] = _spec.TensorRtRtxProfileMax;
        }

        OrtEnv environment = OrtEnv.Instance();
        var devices = new List<OrtEpDevice>();
        foreach (OrtEpDevice device in environment.GetEpDevices())
        {
            if (device.EpName == TensorRtRtxEpName) devices.Add(device);
        }

        if (devices.Count == 0)
            throw new InvalidOperationException("The TensorRT RTX provider registered no devices");

        options.AppendExecutionProvider(environment, devices, providerOptions);
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
            switch (Provider)
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

                case InferenceProvider.TensorRtRtx:
                    AppendTensorRtRtx(options);
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
    /// order the graph declares them. Returns the first output and how long the run took.
    /// </summary>
    internal float[] Run(IReadOnlyList<(float[] Data, long[] Shape)> inputs, out long elapsedNanoseconds)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        InferenceSession? resident = _residentSession;
        if (resident != null) return RunWithSession(resident, inputs, out elapsedNanoseconds);

        lock (ProviderSlotLock)
        {
            ClaimProviderSlot();

            // ClaimProviderSlot promotes the session to _residentSession if the provider turned
            // out to be unusable, in which case there is nothing in the shared slot to run.
            resident = _residentSession;
            return RunWithSession(resident ?? _activeProviderSession!, inputs, out elapsedNanoseconds);
        }
    }

    private float[] RunWithSession(InferenceSession session, IReadOnlyList<(float[] Data, long[] Shape)> inputs,
                                   out long elapsedNanoseconds)
    {
        if (inputs.Count != _inputNames.Count)
        {
            throw new ArgumentException(
                $"Model '{_spec.Name}' expects {_inputNames.Count} inputs ({string.Join(", ", _inputNames)}) but got {inputs.Count}");
        }

        // CreateTensorValueFromMemory pins the caller's array rather than copying it, so the only
        // copy on the way in is the one the execution provider makes onto the device.
        List<OrtValue> values = _inputValues;
        values.Clear();
        try
        {
            foreach ((float[] data, long[] shape) in inputs)
                values.Add(OrtValue.CreateTensorValueFromMemory(data, shape));

            long started = Stopwatch.GetTimestamp();
            using IDisposableReadOnlyCollection<OrtValue> results =
                session.Run(_runOptions, _inputNames, values, _outputNames);
            elapsedNanoseconds = (long)Stopwatch.GetElapsedTime(started).TotalNanoseconds;

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

        InferenceSession session = CreateSession(configured: true);

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
    private static string OptimizeAtRuntime(string sourcePath, string cachePath, string name, bool rebuild = false)
    {
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
            using (new InferenceSession(sourcePath, options))
            {
                // Creating the session writes the optimised graph to disk.
            }

            long sourceSize = new FileInfo(sourcePath).Length;
            long optimizedSize = new FileInfo(tempPath).Length;
            File.Move(tempPath, cachePath, overwrite: true);
            WorkerLog.Notification($"Optimised '{name}' ({Bytes(sourceSize)} -> {Bytes(optimizedSize)})");
            return cachePath;
        }
        catch (Exception e)
        {
            WorkerLog.Warning($"Graph optimisation failed for '{name}', using the unoptimised model: {e.Message}");
            return sourcePath;
        }
    }

    private static bool SamePath(string left, string right) => string.Equals(
        Path.GetFullPath(left), Path.GetFullPath(right),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static string Bytes(long bytes) => bytes >= 1L << 30
        ? $"{bytes / (double)(1L << 30):0.##} GB"
        : $"{bytes / (double)(1L << 20):0.##} MB";

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
        _runOptions.Dispose();
    }
}
