using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
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
/// trades that away on a card too small to hold all three: the graph bytes stay in RAM and a
/// session is created on demand, evicting whichever model held the GPU before.
/// </summary>
public sealed class OnnxModel : IDisposable
{
    private static readonly object GpuSlotLock = new();
    private static OnnxModel _gpuSlotHolder;
    private static InferenceSession _activeGpuSession;

    private readonly string _name;
    private readonly ILogger _logger;
    private readonly byte[] _graphBytes;

    private InferenceSession _residentSession;
    private bool _disposed;

    /// <summary>Names of this graph's inputs, in declaration order.</summary>
    private readonly List<string> _inputNames = new();
    private string[] _outputNames;

    /// <summary>
    /// Reused across runs. Both are only touched while holding this model's run lock, and building
    /// them per call means a native allocation and a managed array on every step of every tile.
    /// </summary>
    private RunOptions _runOptions;
    private readonly List<OrtValue> _inputValues = new();

    private static long _inferenceTicks;
    private static long _inferenceCount;

    private long _modelTicks;
    private long _modelRuns;

    /// <summary>Wall-clock milliseconds spent running this particular graph.</summary>
    public long ModelMillis => Interlocked.Read(ref _modelTicks) * 1000 / Stopwatch.Frequency;

    /// <summary>Number of times this particular graph has been executed.</summary>
    public long ModelRuns => Interlocked.Read(ref _modelRuns);

    public string Name => _name;

    /// <summary>Wall-clock milliseconds spent inside <c>session.Run</c> since startup.</summary>
    public static long TotalInferenceMillis =>
        Interlocked.Read(ref _inferenceTicks) * 1000 / Stopwatch.Frequency;

    /// <summary>Number of graph executions since startup.</summary>
    public static long TotalInferenceCount => Interlocked.Read(ref _inferenceCount);

    public OnnxModel(string modelFilePath, string name, ILogger logger)
    {
        _name = name;
        _logger = logger;

        var stopwatch = Stopwatch.StartNew();
        byte[] sourceBytes = File.ReadAllBytes(modelFilePath);
        _graphBytes = OptimizeAtRuntime(sourceBytes, name, logger);

        // Always create one session up front: it validates the graph and, for the CPU/no-offload
        // paths, is the session used for every run.
        InferenceSession probe = CreateSession(GpuEnabled && DiffusionConfig.Instance.OffloadModels
            ? SessionKind.CpuOnly
            : SessionKind.Configured);

        foreach (string inputName in probe.InputNames) _inputNames.Add(inputName);
        _outputNames = probe.OutputNames.Count > 0 ? new[] { probe.OutputNames[0] } : Array.Empty<string>();

        if (GpuEnabled && DiffusionConfig.Instance.OffloadModels)
        {
            // Only the metadata was needed; sessions are created per GPU slot claim.
            probe.Dispose();
            logger.Notification("[{0}] Model '{1}' prepared ({2}) in {3} ms",
                DiffusionPaths.ModId, name, ModelAssetManager.HumanBytes(_graphBytes.Length), stopwatch.ElapsedMilliseconds);
        }
        else
        {
            _residentSession = probe;
            logger.Notification("[{0}] Model '{1}' loaded on {2} ({3}) in {4} ms",
                DiffusionPaths.ModId, name, ActiveProvider,
                ModelAssetManager.HumanBytes(_graphBytes.Length), stopwatch.ElapsedMilliseconds);
        }
    }

    /// <summary>
    /// Set the first time a GPU session cannot be created (a mismatched CUDA runtime, no DirectML
    /// device, exhausted VRAM). Everything falls back to CPU rather than failing world generation.
    /// </summary>
    private static volatile bool _gpuUnavailable;

    private static bool GpuEnabled => OnnxRuntimeBootstrap.Provider != InferenceProvider.Cpu && !_gpuUnavailable;

    /// <summary>The provider actually in use, which is CPU if the GPU turned out to be unusable.</summary>
    public static InferenceProvider ActiveProvider =>
        _gpuUnavailable ? InferenceProvider.Cpu : OnnxRuntimeBootstrap.Provider;

    private enum SessionKind
    {
        /// <summary>Use whatever execution provider was resolved at startup.</summary>
        Configured,

        /// <summary>Plain CPU session, used only to read graph metadata cheaply.</summary>
        CpuOnly
    }

    private InferenceSession CreateSession(SessionKind kind)
    {
        if (kind == SessionKind.Configured && GpuEnabled)
        {
            try
            {
                return CreateSessionCore(useGpu: true);
            }
            catch (Exception e)
            {
                _gpuUnavailable = true;
                _logger.Warning(
                    "[{0}] The {1} execution provider could not be initialised, so terrain generation will run on the CPU " +
                    "(much slower). Cause: {2}",
                    DiffusionPaths.ModId, OnnxRuntimeBootstrap.Provider, e.Message);
            }
        }

        return CreateSessionCore(useGpu: false);
    }

    private InferenceSession CreateSessionCore(bool useGpu)
    {
        var options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR
        };

        if (useGpu)
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
            return new InferenceSession(_graphBytes, options);
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

        lock (GpuSlotLock)
        {
            ClaimGpuSlot();

            // ClaimGpuSlot promotes the session to _residentSession if the GPU turned out to be
            // unusable, in which case there is nothing in the GPU slot to run.
            resident = _residentSession;
            if (resident != null) return RunWithSession(resident, inputs);

            return RunWithSession(_activeGpuSession, inputs);
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
        long elapsed = 0;
        float[] output;
        try
        {
            foreach ((float[] data, long[] shape) in inputs)
            {
                values.Add(OrtValue.CreateTensorValueFromMemory(data, shape));
            }

            _runOptions ??= new RunOptions();
            long start = Stopwatch.GetTimestamp();
            using IDisposableReadOnlyCollection<OrtValue> results =
                session.Run(_runOptions, _inputNames, values, _outputNames);
            elapsed = Stopwatch.GetTimestamp() - start;

            Interlocked.Add(ref _inferenceTicks, elapsed);
            Interlocked.Increment(ref _inferenceCount);
            Interlocked.Add(ref _modelTicks, elapsed);
            Interlocked.Increment(ref _modelRuns);

            output = results[0].GetTensorDataAsSpan<float>().ToArray();
        }
        finally
        {
            foreach (OrtValue value in values) value.Dispose();
            values.Clear();
        }

        // After the inputs are unpinned and the output copied out, so the idle window really is
        // idle rather than holding the caller's buffers pinned for the GPU.
        InferenceThrottle.AfterRun(elapsed);
        return output;
    }

    /// <summary>
    /// Makes this model the GPU resident one, disposing whichever session held the slot.
    /// Must be called under <see cref="GpuSlotLock"/>.
    /// </summary>
    private void ClaimGpuSlot()
    {
        if (_gpuSlotHolder == this && _activeGpuSession != null) return;

        if (_activeGpuSession != null)
        {
            _activeGpuSession.Dispose();
            _activeGpuSession = null;
            _gpuSlotHolder = null;
        }

        InferenceSession session = CreateSession(SessionKind.Configured);

        if (!GpuEnabled)
        {
            // The GPU provider failed and we got a CPU session instead. Keep it resident: there is
            // no reason to tear it down and rebuild it every time another model runs.
            _residentSession = session;
            return;
        }

        _activeGpuSession = session;
        _gpuSlotHolder = this;
    }

    /// <summary>
    /// Runs the graph optimiser once and caches the result on disk, so later starts skip the
    /// (slow) constant folding and fusion passes. Falls back to the raw graph on any failure.
    /// </summary>
    private static byte[] OptimizeAtRuntime(byte[] sourceBytes, string name, ILogger logger)
    {
        string cachePath = ResolveOptimizedPath(sourceBytes, name);
        try
        {
            if (File.Exists(cachePath)) return File.ReadAllBytes(cachePath);

            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
            string tempPath = cachePath + ".tmp";
            if (File.Exists(tempPath)) File.Delete(tempPath);

            using (var options = new SessionOptions
                   {
                       GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_EXTENDED,
                       OptimizedModelFilePath = tempPath,
                       LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR
                   })
            using (var _ = new InferenceSession(sourceBytes, options))
            {
                // Creating the session writes the optimised graph to disk.
            }

            byte[] optimized = File.ReadAllBytes(tempPath);
            File.Move(tempPath, cachePath, overwrite: true);
            logger.Notification("[{0}] Optimised '{1}' ({2} -> {3})", DiffusionPaths.ModId, name,
                ModelAssetManager.HumanBytes(sourceBytes.Length), ModelAssetManager.HumanBytes(optimized.Length));
            return optimized;
        }
        catch (Exception e)
        {
            logger.Warning("[{0}] Graph optimisation failed for '{1}', using the unoptimised model: {2}",
                DiffusionPaths.ModId, name, e.Message);
            return sourceBytes;
        }
    }

    private static string ResolveOptimizedPath(byte[] sourceBytes, string name)
    {
        string hash = Convert.ToHexString(SHA256.HashData(sourceBytes)).ToLowerInvariant()[..16];
        string fileName = $"{name}-{OnnxRuntimeBootstrap.OnnxRuntimeVersion}-{OnnxRuntimeBootstrap.Provider}-{hash}.onnx"
            .ToLowerInvariant();
        return Path.Combine(DiffusionPaths.OptimizedModelDirectory, fileName);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        lock (GpuSlotLock)
        {
            if (_gpuSlotHolder == this && _activeGpuSession != null)
            {
                _activeGpuSession.Dispose();
                _activeGpuSession = null;
                _gpuSlotHolder = null;
            }
        }

        _residentSession?.Dispose();
        _residentSession = null;
        _runOptions?.Dispose();
        _runOptions = null;
    }
}
