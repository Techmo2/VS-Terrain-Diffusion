using System;
using System.Threading;
using VSTerrainDiffusion.Tensors;
using static VSTerrainDiffusion.Pipeline.Raster;

namespace VSTerrainDiffusion.Pipeline;

/// <summary>
/// The coarse stage: the coarse model's 20-step DPM-Solver++ run over 64-pixel windows of the
/// synthetic conditioning map, giving elevation and climate on the coarse grid.
/// </summary>
public sealed partial class WorldPipeline
{
    /// <summary>
    /// Where finished coarse windows are kept between sessions, or null. Checked before the coarse
    /// model runs for a window, and handed every window it does run for. A window is a pure
    /// function of the world's seed and settings, which the store's own fingerprint covers.
    /// </summary>
    public ICoarseWindowStore CoarseWindowStore { get; set; }

    /// <summary>Coarse windows the model ran for; see <see cref="CoarseWindowsComputed"/>.</summary>
    private long _coarseWindowsComputed;

    /// <summary>Coarse windows this pipeline ran the coarse model for, rather than read back.</summary>
    public long CoarseWindowsComputed => Interlocked.Read(ref _coarseWindowsComputed);

    /// <summary>
    /// Registers the coarse map: 64-pixel windows at a stride of 48, pooled by the world's factor, each the
    /// coarse channels plus the blend weight.
    /// </summary>
    /// <returns>The coarse map.</returns>
    private InfiniteTensor BuildCoarseStage()
    {
        // Pooled tiles are generated on the usual grid and written to a map n times smaller, so
        // window k covers model pixels from k * stride and map pixels from k * stride / n.
        int n = _pooling.Factor;
        int s = CoarseTileSize / n, st = CoarseTileStride / n;
        float[] weights = LinearWeightWindow(s);
        int mapChannels = _coarseChannels + 1;
        var outWindow = new TensorWindow(new[] { mapChannels, s, s }, new[] { mapChannels, st, st });

        return _tileStore.GetOrCreate("base_coarse_map", new int?[] { mapChannels, null, null },
            (windowIndex, _) => StoredCoarseTile(windowIndex, weights, s), outWindow,
            Array.Empty<InfiniteTensor>(), Array.Empty<TensorWindow>());
    }

    /// <summary>
    /// One coarse window, read back from <see cref="CoarseWindowStore"/> when it holds it, else computed and
    /// handed to it.
    /// </summary>
    /// <param name="windowIndex">The window's index (channel, I, J).</param>
    /// <param name="weights">The blend window.</param>
    /// <param name="size">Edge length after pooling.</param>
    /// <returns>The window.</returns>
    private FloatTensor StoredCoarseTile(int[] windowIndex, float[] weights, int size)
    {
        ICoarseWindowStore store = CoarseWindowStore;
        int mapChannels = _coarseChannels + 1;
        int length = mapChannels * size * size;
        float[] stored = store?.TryLoad(windowIndex[1], windowIndex[2], length);
        if (stored != null) return new FloatTensor(new[] { mapChannels, size, size }, stored);

        FloatTensor computed = CoarseTile(windowIndex, weights);
        Interlocked.Increment(ref _coarseWindowsComputed);
        store?.Save(windowIndex[1], windowIndex[2], computed.Data, mapChannels);
        return computed;
    }

    /// <summary>
    /// Runs the coarse model over one window: builds its conditioning from the synthetic map, denoises it,
    /// and weights it for blending.
    /// </summary>
    /// <param name="windowIndex">The window's index (channel, I, J).</param>
    /// <param name="weights">The blend window.</param>
    /// <returns>The window: coarse channels then the weight, each pooled.</returns>
    private FloatTensor CoarseTile(int[] windowIndex, float[] weights)
    {
        const int s = CoarseTileSize, st = CoarseTileStride;
        int plane = s * s;
        int i1 = windowIndex[1] * st, j1 = windowIndex[2] * st;
        int channels = _coarseChannels;

        // Synthetic conditioning: [elevSqrt, temp, tempStd, precip, precipStd].
        // Python samples with the coordinates intentionally swapped.
        float[] synthetic = _syntheticMapFactory.Sample(j1, i1, j1 + s, i1 + s);

        // Stretch the cold tail of the temperature channel, matching the Python pipeline.
        for (int px = 0; px < plane; px++)
        {
            float v = synthetic[plane + px];
            if (v <= 20f) synthetic[plane + px] = (v - 20f) * 1.25f + 20f;
        }

        // Normalise using coarse means/stds at channel indices [0, 2, 3, 4, 5].
        int[] meanIndex = { 0, 2, 3, 4, 5 };
        var condImg = new float[5 * plane];
        for (int ch = 0; ch < 5; ch++)
        {
            float mean = _modelMeans[meanIndex[ch]], std = _modelStds[meanIndex[ch]];
            for (int px = 0; px < plane; px++)
                condImg[ch * plane + px] = (synthetic[ch * plane + px] - mean) / std;
        }

        // Rotate each conditioning channel against noise by its own cond_snr. Note the direction:
        // cos falls and sin rises with the angle, so a *larger* cond_snr leaves *less* of the
        // conditioning behind, and the value is really a noise level however it is named upstream.
        float[] condNoise = GaussianNoisePatch.Generate(_seed, i1, j1, s, s, 5, s, s);
        var condMixed = new float[5 * plane];
        // Structure sites may bind the elevation conditioning tighter where they stand.
        float[] elevationLevels = CoarseConditioningSite.ElevationNoiseLevels(_sites, _condSnr[0], i1, j1, s);
        for (int ch = 0; ch < 5; ch++)
        {
            float t = (float)Math.Atan(_condSnr[ch]);
            float cosT = (float)Math.Cos(t), sinT = (float)Math.Sin(t);
            for (int px = 0; px < plane; px++)
            {
                int k = ch * plane + px;
                if (ch == 0 && elevationLevels != null)
                {
                    float tp = (float)Math.Atan(elevationLevels[px]);
                    condMixed[k] = (float)Math.Cos(tp) * condImg[k] + (float)Math.Sin(tp) * condNoise[k];
                    continue;
                }
                condMixed[k] = cosT * condImg[k] + sinT * condNoise[k];
            }
        }

        var scheduler = new EdmScheduler(20);
        float[] sample = GaussianNoisePatch.Generate(_seed + 1, i1, j1, s, s, channels, s, s);
        for (int k = 0; k < sample.Length; k++) sample[k] *= scheduler.Sigmas[0];

        var condInputs = new float[5][];
        var condShapes = new long[5][];
        for (int ci = 0; ci < 5; ci++)
        {
            condInputs[ci] = new[] { _condVals[ci] };
            condShapes[ci] = new long[] { 1 };
        }

        var xIn = new float[(channels + 5) * plane];
        Array.Copy(condMixed, 0, xIn, channels * plane, 5 * plane);

        for (int step = 0; step < 20; step++)
        {
            float sigma = scheduler.Sigmas[step];
            float cNoise = EdmScheduler.TrigflowPreconditionNoise(sigma);
            float[] scaledIn = EdmScheduler.PreconditionInputs(sample, sigma);
            Array.Copy(scaledIn, 0, xIn, 0, channels * plane);

            InferencePreemption.ThrowIfPreempted();
            float[] modelOut = _coarseModel.RunModel(
                xIn, new long[] { 1, channels + 5, s, s }, new[] { cNoise }, condInputs, condShapes);
            sample = scheduler.Step(modelOut, sample);
        }

        // Denormalise, then turn channel 1 into the p5 difference.
        var output = new float[channels * plane];
        for (int ch = 0; ch < channels; ch++)
        {
            float mean = _modelMeans[ch], std = _modelStds[ch];
            for (int px = 0; px < plane; px++)
                output[ch * plane + px] = (sample[ch * plane + px] / SigmaData) * std + mean;
        }
        if (_coarseHeightNoise > 0f) AddCoarseHeightNoise(output, i1, j1, s);
        for (int px = 0; px < plane; px++) output[plane + px] = output[px] - output[plane + px];
        CoarseConditioningSite.ApplyRelief(_sites, output, i1, j1, s);

        int size = s;
        if (!_pooling.IsNone)
        {
            output = Pool(output, channels, s, _pooling);
            size = s / _pooling.Factor;
            plane = size * size;
        }

        var result = new FloatTensor(new[] { channels + 1, size, size });
        for (int ch = 0; ch < channels; ch++)
            for (int px = 0; px < plane; px++)
                result.Data[ch * plane + px] = output[ch * plane + px] * weights[px];
        Array.Copy(weights, 0, result.Data, channels * plane, plane);
        return result;
    }

    /// <summary>
    /// Adds <see cref="_coarseHeightNoise"/> to the elevation channel of a denormalised coarse
    /// output, before the fifth percentile is taken from it so the relief moves with it. The draw
    /// is keyed to world position, so overlapping tiles agree and blend without seams. Land only,
    /// faded in over the first <see cref="CoarseNoiseShoreRamp"/> above sea level, so it never
    /// raises or sinks a coastline.
    /// </summary>
    private void AddCoarseHeightNoise(float[] output, int i1, int j1, int s)
    {
        float[] noise = GaussianNoisePatch.Generate(_seed + 7331, i1, j1, s, s, 1, s, s);
        for (int px = 0; px < s * s; px++)
        {
            float elevation = output[px];
            if (elevation <= 0f) continue;
            float fade = Math.Min(1f, elevation / CoarseNoiseShoreRamp);
            output[px] = Math.Max(0f, elevation + noise[px] * _coarseHeightNoise * fade);
        }
    }

    /// <summary>
    /// Pools a (channels, s, s) coarse output to (channels, s/n, s/n), as the reference's
    /// <c>_pool_coarse_conditioning</c>: averages everywhere, or, for an extreme pool, the highest
    /// elevation and the lowest fifth percentile in each block.
    /// </summary>
    private static float[] Pool(float[] input, int channels, int s, CoarsePooling pooling)
    {
        int n = pooling.Factor, size = s / n, plane = s * s, pooledPlane = size * size;
        var output = new float[channels * pooledPlane];
        for (int ch = 0; ch < channels; ch++)
        {
            bool max = pooling.Extreme && ch == 0, min = pooling.Extreme && ch == 1;
            for (int pi = 0; pi < size; pi++)
            for (int pj = 0; pj < size; pj++)
            {
                float acc = max ? float.MinValue : min ? float.MaxValue : 0f;
                for (int di = 0; di < n; di++)
                for (int dj = 0; dj < n; dj++)
                {
                    float v = input[ch * plane + (pi * n + di) * s + pj * n + dj];
                    acc = max ? Math.Max(acc, v) : min ? Math.Min(acc, v) : acc + v;
                }
                output[ch * pooledPlane + pi * size + pj] = max || min ? acc : acc / (n * n);
            }
        }
        return output;
    }
}
