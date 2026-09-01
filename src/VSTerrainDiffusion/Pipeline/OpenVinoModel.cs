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
    private readonly object _gate = new();
    private readonly string _fallbackModelPath;
    private readonly string _name;
    private readonly ILogger _logger;
    private OpenVinoWorkerClient _runtime;
    private OnnxModel _fallback;
    private Exception _terminalFailure;
    private string _backend;
    private bool _disposed;
    private long _runCount;
    private long _runItems;
    private long _runStopwatchTicks;

    public OpenVinoModel(string modelPath, string fallbackModelPath, string name, ILogger logger)
        : this(modelPath, fallbackModelPath, name, logger, CancellationToken.None)
    {
    }

    internal OpenVinoModel(string modelPath, string fallbackModelPath, string name, ILogger logger,
                           CancellationToken cancellation)
    {
        _fallbackModelPath = fallbackModelPath ?? modelPath;
        _name = name;
        _logger = logger;
        var stopwatch = Stopwatch.StartNew();
        string cacheDirectory = Path.Combine(DiffusionPaths.OptimizedModelDirectory, "openvino");
        _runtime = new OpenVinoWorkerClient(
            modelPath, name, cacheDirectory, OnnxRuntimeBootstrap.OpenVinoDirectory,
            Environment.ProcessorCount,
            message => logger.Notification("[{0}] OpenVINO '{1}': {2}", DiffusionPaths.ModId, name, message),
            cancellation);
        _backend = $"OpenVINO {OnnxRuntimeBootstrap.OpenVinoVersion}";
        logger.Notification("[{0}] Model '{1}' compiled by OpenVINO ({2}) in {3} ms",
            DiffusionPaths.ModId, name, ModelAssetManager.HumanBytes(new FileInfo(modelPath).Length),
            stopwatch.ElapsedMilliseconds);
    }

    public string Backend => _backend;

    public long RunCount => Interlocked.Read(ref _runCount);

    public long RunItems => Interlocked.Read(ref _runItems);

    public long RunMilliseconds =>
        Interlocked.Read(ref _runStopwatchTicks) * 1000 / Stopwatch.Frequency;

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
                    return _runtime.Run(inputs);
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
        }
    }

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
            _fallback = new OnnxModel(_fallbackModelPath, _name, _logger);
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
