using System;
using VSTerrainDiffusion.Core;
using VSTerrainDiffusion.Tensors;

namespace VSTerrainDiffusion.Pipeline;

/// <summary>
/// The ONNX pipeline embedded in this mod, presented as a terrain source.
///
/// This runs the same models the Terrain Diffusion API runs, in-process, so a server can generate
/// worlds without a Python service. It also exposes the coarse map directly, which the API cannot,
/// making the spawn search and terrain height survey far cheaper than probing at full detail.
/// </summary>
public sealed class LocalTerrainSource : ITerrainSource, ICoarseMapSource
{
    private readonly WorldPipeline _pipeline;

    public float NativeResolutionMeters { get; }

    public LocalTerrainSource(ulong seed, PipelineModels models)
    {
        _pipeline = new WorldPipeline(seed, models);
        NativeResolutionMeters = WorldPipelineModelConfig.Instance.NativeResolution;
    }

    public TerrainSample Fetch(int i1, int j1, int i2, int j2, bool withClimate)
    {
        WorldPipeline.Sample sample = _pipeline.Get(i1, j1, i2, j2, withClimate);
        int h = i2 - i1, w = j2 - j1;

        // The pipeline carries a fifth plane, the local lapse rate, which the API does not expose
        // and nothing above this interface uses; the first four planes are identical.
        float[] climate = null;
        if (withClimate && sample.Climate != null)
        {
            climate = new float[TerrainSample.ClimateChannels * h * w];
            Array.Copy(sample.Climate, climate, climate.Length);
        }

        return new TerrainSample(h, w, sample.Elevation, climate);
    }

    public FloatTensor GetCoarseSlice(int ci0, int cj0, int ci1, int cj1)
        => _pipeline.GetCoarseSlice(ci0, cj0, ci1, cj1);

    public int CoarseCellNativePixels => 32 * WorldPipelineModelConfig.Instance.LatentCompression;

    public string Describe() =>
        $"embedded ONNX pipeline ({NativeResolutionMeters:0.##} m per model pixel)";

    public void Dispose()
    {
    }
}
