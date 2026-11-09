using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Vintagestory.API.Common;
using VSTerrainDiffusion.Core;
using VSTerrainDiffusion.Native;

namespace VSTerrainDiffusion.Pipeline;

/// <summary>
/// A single ONNX graph, run in the <see cref="InferenceWorker"/>. The session, its execution
/// provider and the fall back to the CPU all live there; this side names the graph, sends the
/// tensors and keeps the counts.
/// </summary>
public sealed class OnnxModel : IModelRunner
{
    /// <summary>Backing field of <see cref="ActiveProvider"/>.</summary>
    private static volatile InferenceProvider _activeProvider = InferenceProvider.Cpu;

    /// <summary>The worker process the session lives in.</summary>
    private readonly InferenceWorker _worker;

    /// <summary>The worker's handle for this model.</summary>
    private readonly int _id;

    /// <summary>Set once the worker has been told to unload the model.</summary>
    private bool _disposed;

    /// <summary>Backing field of <see cref="RunCount"/>.</summary>
    private long _runCount;

    /// <summary>Backing field of <see cref="RunItems"/>.</summary>
    private long _runItems;

    /// <summary>Run time reported by the worker, summed.</summary>
    private long _runNanoseconds;

    /// <summary>
    /// Loads a graph into the worker, which creates its session on the worker's provider (or the CPU if that
    /// fails).
    /// </summary>
    /// <param name="worker">The worker to load into.</param>
    /// <param name="modelFilePath">The .onnx file.</param>
    /// <param name="name">
    /// The model's role ("coarse", "base" or "decoder"); names the optimised cache file and the TensorRT RTX
    /// shape profile.
    /// </param>
    /// <param name="logger">The server log.</param>
    /// <param name="cancellation">Abandons the load.</param>
    public OnnxModel(InferenceWorker worker, string modelFilePath, string name, ILogger logger,
                     CancellationToken cancellation = default)
    {
        _worker = worker;
        InferenceSettings settings = InferenceSettings.Current;
        (string Min, string Max)? profile = TensorRtShapeProfiles.For(name);

        var spec = new ModelSpec
        {
            Name = name,
            SourcePath = Path.GetFullPath(modelFilePath),
            // TensorRT RTX cannot import ONNX Runtime's contrib ops (QuickGelu and friends), and
            // every fused node it cannot take splits the engine. Hand it the model as exported, and
            // MIGraphX likewise, which compiles whole graphs and has no kernels for those ops either.
            OptimizedPath = worker.Provider is InferenceProvider.TensorRtRtx or InferenceProvider.Rocm
                ? null
                : OptimizedPath(modelFilePath, name, worker.Provider),
            LoadFromFile = settings.ModelLoadMode != "memory",
            Offload = settings.OffloadModels,
            CpuThreads = settings.CpuThreadsOr(0),
            TensorRtRtxCacheDirectory = OnnxRuntimeBootstrap.TensorRtRtxCacheDirectory,
            RocmCacheDirectory = OnnxRuntimeBootstrap.RocmCacheDirectory,
            TensorRtRtxProfileMin = profile?.Min,
            TensorRtRtxProfileMax = profile?.Max
        };

        (_id, ModelInfo info) = worker.LoadModel(spec, cancellation);
        Backend = info.Backend;
        _activeProvider = info.ActiveProvider;
    }

    /// <summary>
    /// The provider the latest model loaded on, which is the CPU if the requested one could not
    /// create a session. Sizes the pipeline's batches and tiles.
    /// </summary>
    public static InferenceProvider ActiveProvider => _activeProvider;

    /// <inheritdoc/>
    public string Backend { get; }

    /// <summary>Number of completed inference calls since this model was loaded.</summary>
    public long RunCount => Interlocked.Read(ref _runCount);

    /// <summary>Milliseconds spent inside ONNX Runtime, in the worker, not counting the trip there.</summary>
    public long RunMilliseconds => Interlocked.Read(ref _runNanoseconds) / 1_000_000;

    /// <summary>Total batch items processed by completed inference calls.</summary>
    public long RunItems => Interlocked.Read(ref _runItems);

    /// <summary>Runs the graph with the pipeline's (x, noise_labels, cond_0..cond_n) signature.</summary>
    public float[] RunModel(float[] x, long[] xShape, float[] noiseLabels,
                            float[][] condInputs, long[][] condShapes)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        int condCount = condInputs?.Length ?? 0;
        var inputs = new List<(float[], long[])>(2 + condCount)
        {
            (x, xShape),
            (noiseLabels, new long[] { noiseLabels.Length })
        };
        for (int i = 0; i < condCount; i++) inputs.Add((condInputs[i], condShapes[i]));

        float[] output = _worker.Run(_id, inputs, out long elapsedNanoseconds);

        Interlocked.Add(ref _runNanoseconds, elapsedNanoseconds);
        Interlocked.Increment(ref _runCount);
        Interlocked.Add(ref _runItems, xShape[0]);

        // The device was busy for the run itself; the trip through the pipes left it idle.
        InferenceThrottle.AfterRun(elapsedNanoseconds * Stopwatch.Frequency / 1_000_000_000);
        return output;
    }

    /// <summary>
    /// Where the worker caches this graph after ONNX Runtime's optimiser has run over it, keyed by
    /// the graph's contents, the runtime version and the provider it was optimised for.
    /// </summary>
    private static string OptimizedPath(string sourcePath, string name, InferenceProvider provider)
    {
        string hash = ModelAssetManager.Sha256Hex(sourcePath)[..16];
        if (provider == InferenceProvider.OpenVino) provider = InferenceProvider.Cpu;
        string fileName = $"{name}-{OnnxRuntimeBootstrap.OnnxRuntimeVersion}-{provider}-{hash}.onnx"
            .ToLowerInvariant();
        return Path.Combine(DiffusionPaths.OptimizedModelDirectory, fileName);
    }

    /// <summary>Unloads the model from the worker. Safe to call twice.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _worker.Unload(_id);
    }
}
