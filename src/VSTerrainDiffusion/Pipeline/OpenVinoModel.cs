using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Vintagestory.API.Common;
using VSTerrainDiffusion.Core;
using VSTerrainDiffusion.Native;

namespace VSTerrainDiffusion.Pipeline;

/// <summary>A model hosted by an isolated OpenVINO worker.</summary>
public sealed class OpenVinoModel : IModelRunner
{
    /// <summary>Serialises runs, the switch to the CPU and disposal.</summary>
    private readonly object _gate = new();

    /// <summary>The ONNX Runtime worker, for the CPU fallback.</summary>
    private readonly InferenceWorker _worker;

    /// <summary>The graph the CPU fallback loads.</summary>
    private readonly string _fallbackModelPath;

    /// <summary>The model's role ("coarse", "base" or "decoder").</summary>
    private readonly string _name;

    /// <summary>The server log.</summary>
    private readonly ILogger _logger;

    /// <summary>The OpenVINO worker; null after a failure or disposal.</summary>
    private OpenVinoWorkerClient _runtime;

    /// <summary>The ONNX Runtime CPU model, once OpenVINO has failed.</summary>
    private OnnxModel _fallback;

    /// <summary>Set when both OpenVINO and the fallback failed; every later run throws it.</summary>
    private Exception _terminalFailure;

    /// <summary>Backing field of <see cref="Backend"/>.</summary>
    private string _backend;

    /// <summary>Set by <see cref="Dispose"/>.</summary>
    private bool _disposed;

    /// <summary>Backing field of <see cref="RunCount"/>.</summary>
    private long _runCount;

    /// <summary>Backing field of <see cref="RunItems"/>.</summary>
    private long _runItems;

    /// <summary>Wall time of every run, in <see cref="System.Diagnostics.Stopwatch"/> ticks.</summary>
    private long _runStopwatchTicks;

    /// <summary>Compiles a graph in a new OpenVINO worker process.</summary>
    /// <param name="worker">The ONNX Runtime worker, kept for the CPU fallback.</param>
    /// <param name="modelPath">The graph OpenVINO compiles.</param>
    /// <param name="fallbackModelPath">
    /// The graph ONNX Runtime loads if OpenVINO fails later; null to reuse <paramref name="modelPath"/>.
    /// </param>
    /// <param name="name">The model's role.</param>
    /// <param name="logger">The server log.</param>
    /// <param name="cancellation">Abandons the compile.</param>
    internal OpenVinoModel(InferenceWorker worker, string modelPath, string fallbackModelPath, string name,
                           ILogger logger, CancellationToken cancellation)
    {
        _worker = worker;
        _fallbackModelPath = fallbackModelPath ?? modelPath;
        _name = name;
        _logger = logger;
        var stopwatch = Stopwatch.StartNew();
        string cacheDirectory = Path.Combine(DiffusionPaths.OptimizedModelDirectory, "openvino");
        _runtime = new OpenVinoWorkerClient(
            modelPath, name, cacheDirectory, OnnxRuntimeBootstrap.OpenVinoDirectory,
            InferenceSettings.Current.CpuThreadsOr(Environment.ProcessorCount),
            message => logger.Notification("[{0}] OpenVINO '{1}': {2}", DiffusionPaths.ModId, name, message),
            cancellation);
        _backend = $"OpenVINO {OnnxRuntimeBootstrap.OpenVinoVersion}";
        logger.Notification("[{0}] Model '{1}' compiled by OpenVINO ({2}) in {3} ms",
            DiffusionPaths.ModId, name, ModelAssetManager.HumanBytes(new FileInfo(modelPath).Length),
            stopwatch.ElapsedMilliseconds);
    }

    /// <inheritdoc/>
    public string Backend => _backend;

    /// <inheritdoc/>
    public long RunCount => Interlocked.Read(ref _runCount);

    /// <inheritdoc/>
    public long RunItems => Interlocked.Read(ref _runItems);

    /// <inheritdoc/>
    public long RunMilliseconds =>
        Interlocked.Read(ref _runStopwatchTicks) * 1000 / Stopwatch.Frequency;

    /// <inheritdoc/>
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

        long started = Stopwatch.GetTimestamp();
        long throttleTicks = 0;
        try
        {
            lock (_gate)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(OpenVinoModel));
                if (_terminalFailure != null)
                    throw new InvalidOperationException($"Model '{_name}' is unavailable", _terminalFailure);
                if (_fallback != null)
                    return _fallback.RunModel(x, xShape, noiseLabels, condInputs, condShapes);

                try
                {
                    long inferenceStarted = Stopwatch.GetTimestamp();
                    float[] output = _runtime.Run(inputs);
                    throttleTicks = Stopwatch.GetTimestamp() - inferenceStarted;
                    return output;
                }
                catch (OpenVinoWorkerException openVinoFailure)
                {
                    return SwitchToCpuAndRetry(
                        openVinoFailure, x, xShape, noiseLabels, condInputs, condShapes);
                }
            }
        }
        finally
        {
            Interlocked.Increment(ref _runCount);
            Interlocked.Add(ref _runItems,
                xShape != null && xShape.Length > 0 ? xShape[0] : 0);
            Interlocked.Add(ref _runStopwatchTicks, Stopwatch.GetTimestamp() - started);

            // A fallback OnnxModel applies the limiter itself. Only the successful worker path
            // reaches here with throttleTicks set, so provider fallback is not throttled twice.
            if (throttleTicks > 0) InferenceThrottle.AfterRun(throttleTicks);
        }
    }

    /// <summary>
    /// Replaces the failed OpenVINO worker with an ONNX Runtime CPU model for good, then reruns the call on
    /// it. Called under <see cref="_gate"/>.
    /// </summary>
    /// <param name="openVinoFailure">Why OpenVINO failed.</param>
    /// <param name="x">See <see cref="RunModel"/>.</param>
    /// <param name="xShape">See <see cref="RunModel"/>.</param>
    /// <param name="noiseLabels">See <see cref="RunModel"/>.</param>
    /// <param name="condInputs">See <see cref="RunModel"/>.</param>
    /// <param name="condShapes">See <see cref="RunModel"/>.</param>
    /// <returns>The CPU model's output.</returns>
    /// <exception cref="InvalidOperationException">The CPU model could not be loaded either.</exception>
    private float[] SwitchToCpuAndRetry(OpenVinoWorkerException openVinoFailure,
                                         float[] x, long[] xShape, float[] noiseLabels,
                                         float[][] condInputs, long[][] condShapes)
    {
        _logger.Warning(
            "[{0}] OpenVINO failed while running '{1}' ({2}); switching this model permanently to ONNX Runtime CPU. " +
            "This provider change can alter newly generated terrain slightly.",
            DiffusionPaths.ModId, _name, openVinoFailure.Message);
        _runtime.Dispose();
        _runtime = null;

        try
        {
            _fallback = new OnnxModel(_worker, _fallbackModelPath, _name, _logger);
            _backend = _fallback.Backend + " (OpenVINO fallback)";
        }
        catch (Exception fallbackFailure)
        {
            _backend = "unavailable";
            _terminalFailure = new AggregateException(
                "OpenVINO failed and the ONNX Runtime CPU fallback could not be loaded",
                openVinoFailure, fallbackFailure);
            throw new InvalidOperationException($"Model '{_name}' is unavailable", _terminalFailure);
        }

        return _fallback.RunModel(x, xShape, noiseLabels, condInputs, condShapes);
    }

    /// <summary>Ends the OpenVINO worker, or unloads the fallback. Safe to call twice.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _runtime?.Dispose();
            _runtime = null;
            _fallback?.Dispose();
            _fallback = null;
        }
    }
}
