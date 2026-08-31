using System;

namespace VSTerrainDiffusion.Pipeline;

/// <summary>The inference operations and counters used by the terrain pipeline.</summary>
public interface IModelRunner : IDisposable
{
    string Backend { get; }
    long RunCount { get; }
    long RunItems { get; }
    long RunMilliseconds { get; }

    float[] RunModel(float[] x, long[] xShape, float[] noiseLabels,
                     float[][] condInputs, long[][] condShapes);
}
