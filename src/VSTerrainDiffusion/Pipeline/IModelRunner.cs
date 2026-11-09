using System;

namespace VSTerrainDiffusion.Pipeline;

/// <summary>The inference operations and counters used by the terrain pipeline.</summary>
public interface IModelRunner : IDisposable
{
    /// <summary>What runs the graph, for the log and <c>/tdiff</c> reports, e.g. "ONNX Runtime CUDA".</summary>
    string Backend { get; }

    /// <summary>Number of completed inference calls since the model was loaded.</summary>
    long RunCount { get; }

    /// <summary>Total batch items processed by completed inference calls.</summary>
    long RunItems { get; }

    /// <summary>Milliseconds spent running the graph.</summary>
    long RunMilliseconds { get; }

    /// <summary>Runs the graph with the pipeline's (x, noise_labels, cond_0..cond_n) signature.</summary>
    /// <param name="x">The noisy input, NCHW.</param>
    /// <param name="xShape">Shape of <paramref name="x"/>; its first dimension is the batch size.</param>
    /// <param name="noiseLabels">One noise level per batch item.</param>
    /// <param name="condInputs">The conditioning tensors, in graph input order; may be null.</param>
    /// <param name="condShapes">Shape of each conditioning tensor.</param>
    /// <returns>The graph's single output, flattened.</returns>
    float[] RunModel(float[] x, long[] xShape, float[] noiseLabels,
                     float[][] condInputs, long[][] condShapes);
}
