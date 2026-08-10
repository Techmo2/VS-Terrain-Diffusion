using System;
using System.Collections.Generic;
using VSTerrainDiffusion.Core;
using VSTerrainDiffusion.Tensors;

namespace VSTerrainDiffusion.Pipeline;

/// <summary>
/// Port of terrain_diffusion/inference/world_pipeline.py.
///
/// Three stages feed each other through <see cref="InfiniteTensor"/> caches:
/// a coarse map (20-step DPM-Solver++), a latent map (2 flow-matching steps) and a decoder
/// (1 flow-matching step). All pixel coordinates are in native model resolution
/// (<see cref="WorldPipelineModelConfig.NativeResolution"/> metres per pixel).
/// </summary>
public sealed class WorldPipeline
{
    private const int CoarseTileSize = 64;
    private const int CoarseTileStride = 48;
    private const int LatentTileSize = 64;
    private const int LatentTileStride = 32;
    private const int DecoderTileSize = 256;
    private const int DecoderTileStride = 192;

    private const float SigmaData = EdmScheduler.SigmaData;

    private static readonly float[] CondMeans = { 14.99f, 11.65f, 15.87f, 619.26f, 833.12f, 69.40f, 0.66f };
    private static readonly float[] CondStds = { 21.72f, 21.78f, 10.40f, 452.29f, 738.09f, 34.59f, 0.47f };

    private const float LowFreqMean = -31.4f;
    private const float LowFreqStd = 38.6f;

    /// <summary>mp_concat component widths: means, p5, climate, mask, histogram, noise level.</summary>
    private static readonly int[] CondDims = { 16, 16, 4, 16, 5, 1 };

    private readonly WorldPipelineModelConfig _config;
    private readonly int _latentCompression;
    private readonly float[] _modelMeans;
    private readonly float[] _modelStds;
    private readonly float[] _condSnr;
    private readonly float[] _condVals;
    private readonly float[] _mpConcatScales;
    private readonly float[] _histogramRaw;
    private readonly float _residualMean;
    private readonly float _residualStd;

    private readonly OnnxModel _coarseModel;
    private readonly OnnxModel _baseModel;
    private readonly OnnxModel _decoderModel;

    private readonly MemoryTileStore _tileStore;
    private readonly long _cacheLimitBytes;

    private readonly InfiniteTensor _coarse;
    private readonly InfiniteTensor _latents;
    private readonly InfiniteTensor _residual;

    private SyntheticMapFactory _syntheticMapFactory;
    private ulong _seed;

    public WorldPipeline(ulong seed, PipelineModels models)
    {
        _seed = seed;
        _config = WorldPipelineModelConfig.Instance;

        if (_config.CoarsePooling != 1)
        {
            throw new NotSupportedException(
                "coarse_pooling=" + _config.CoarsePooling + " is not supported by this pipeline");
        }

        _latentCompression = _config.LatentCompression;
        _modelMeans = _config.CoarseMeans;
        _modelStds = _config.CoarseStds;
        _condSnr = _config.CondSnr;
        _residualMean = _config.ResidualMean;
        _residualStd = _config.ResidualStd;
        _histogramRaw = _config.HistogramRaw ?? new float[5];

        _condVals = new float[_condSnr.Length];
        for (int i = 0; i < _condSnr.Length; i++) _condVals[i] = (float)Math.Log(_condSnr[i] / 8.0);

        _mpConcatScales = BuildMpConcatScales();

        _coarseModel = models.Coarse;
        _baseModel = models.Base;
        _decoderModel = models.Decoder;

        _syntheticMapFactory = new SyntheticMapFactory(seed);
        _tileStore = new MemoryTileStore();
        _cacheLimitBytes = Math.Max(32L, DiffusionConfig.Instance.TileCacheMegabytes) * 1024 * 1024;

        _coarse = BuildCoarseStage();
        _latents = BuildLatentStage();
        _residual = BuildDecoderStage();
    }

    public ulong Seed => _seed;

    public float NativeResolution => _config.NativeResolution;

    public long TotalComputedWindowCount => _tileStore.TotalComputedWindowCount;

    /// <summary>Switches to a different world seed and drops every cached window.</summary>
    public void SetSeed(ulong newSeed)
    {
        if (newSeed == _seed) return;
        _seed = newSeed;
        _syntheticMapFactory = new SyntheticMapFactory(newSeed);
        _tileStore.ClearAllCaches();
    }

    private static float[] BuildMpConcatScales()
    {
        int sumN = 0;
        foreach (int n in CondDims) sumN += n;
        int k = CondDims.Length;
        float c = (float)Math.Sqrt((double)sumN * k);
        var scales = new float[k];
        for (int i = 0; i < k; i++) scales[i] = c / (float)Math.Sqrt(CondDims[i]) / k;
        return scales;
    }

    // =====================================================================
    // Coarse stage
    // =====================================================================

    private InfiniteTensor BuildCoarseStage()
    {
        const int s = CoarseTileSize, st = CoarseTileStride;
        float[] weights = LinearWeightWindow(s);
        var outWindow = new TensorWindow(new[] { 7, s, s }, new[] { 7, st, st });

        return _tileStore.GetOrCreate("base_coarse_map", new int?[] { 7, null, null },
            (windowIndex, _) => CoarseTile(windowIndex, weights), outWindow,
            Array.Empty<InfiniteTensor>(), Array.Empty<TensorWindow>(), _cacheLimitBytes);
    }

    private FloatTensor CoarseTile(int[] windowIndex, float[] weights)
    {
        const int s = CoarseTileSize, st = CoarseTileStride;
        int plane = s * s;
        int i1 = windowIndex[1] * st, j1 = windowIndex[2] * st;

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

        float[] condNoise = GaussianNoisePatch.Generate(_seed, i1, j1, s, s, 5, s, s);
        var condMixed = new float[5 * plane];
        for (int ch = 0; ch < 5; ch++)
        {
            float t = (float)Math.Atan(_condSnr[ch]);
            float cosT = (float)Math.Cos(t), sinT = (float)Math.Sin(t);
            for (int px = 0; px < plane; px++)
            {
                int k = ch * plane + px;
                condMixed[k] = cosT * condImg[k] + sinT * condNoise[k];
            }
        }

        var scheduler = new EdmScheduler(20);
        float[] sample = GaussianNoisePatch.Generate(_seed + 1, i1, j1, s, s, 6, s, s);
        for (int k = 0; k < sample.Length; k++) sample[k] *= scheduler.Sigmas[0];

        var condInputs = new float[5][];
        var condShapes = new long[5][];
        for (int ci = 0; ci < 5; ci++)
        {
            condInputs[ci] = new[] { _condVals[ci] };
            condShapes[ci] = new long[] { 1 };
        }

        var xIn = new float[11 * plane];
        Array.Copy(condMixed, 0, xIn, 6 * plane, 5 * plane);

        for (int step = 0; step < 20; step++)
        {
            float sigma = scheduler.Sigmas[step];
            float cNoise = EdmScheduler.TrigflowPreconditionNoise(sigma);
            float[] scaledIn = EdmScheduler.PreconditionInputs(sample, sigma);
            Array.Copy(scaledIn, 0, xIn, 0, 6 * plane);

            float[] modelOut = _coarseModel.RunModel(
                xIn, new long[] { 1, 11, s, s }, new[] { cNoise }, condInputs, condShapes);
            sample = scheduler.Step(modelOut, sample);
        }

        // Denormalise, then turn channel 1 into the p5 difference.
        var output = new float[6 * plane];
        for (int ch = 0; ch < 6; ch++)
        {
            float mean = _modelMeans[ch], std = _modelStds[ch];
            for (int px = 0; px < plane; px++)
                output[ch * plane + px] = (sample[ch * plane + px] / SigmaData) * std + mean;
        }
        for (int px = 0; px < plane; px++) output[plane + px] = output[px] - output[plane + px];

        var result = new FloatTensor(new[] { 7, s, s });
        for (int ch = 0; ch < 6; ch++)
            for (int px = 0; px < plane; px++)
                result.Data[ch * plane + px] = output[ch * plane + px] * weights[px];
        Array.Copy(weights, 0, result.Data, 6 * plane, plane);
        return result;
    }

    // =====================================================================
    // Latent stage
    // =====================================================================

    private InfiniteTensor BuildLatentStage()
    {
        const int s = LatentTileSize, st = LatentTileStride;
        var outWindow = new TensorWindow(new[] { 6, s, s }, new[] { 6, st, st });
        var coarseWindow = new TensorWindow(new[] { 7, 4, 4 }, new[] { 7, 1, 1 }, new[] { 0, -1, -1 });
        float[] weights = LinearWeightWindow(s);

        float initialT = (float)Math.Atan(EdmScheduler.SigmaMax / SigmaData);
        InfiniteTensor initialLatent = _tileStore.GetOrCreateBatched(
            "init_latent_map", new int?[] { 6, null, null },
            (windowIndices, args) => LatentBatch(windowIndices, null, args[0], initialT, 5819, weights),
            outWindow, new[] { _coarse }, new[] { coarseWindow }, _cacheLimitBytes, 4);

        float intermediateT = (float)Math.Atan(0.35f / SigmaData);
        return _tileStore.GetOrCreateBatched(
            "step_latent_map_0", new int?[] { 6, null, null },
            (windowIndices, args) => LatentBatch(windowIndices, args[0], args[1], intermediateT, 5820, weights),
            outWindow, new[] { initialLatent, _coarse }, new[] { outWindow, coarseWindow }, _cacheLimitBytes, 4);
    }

    private IReadOnlyList<FloatTensor> LatentBatch(IReadOnlyList<int[]> windowIndices,
                                                   IReadOnlyList<FloatTensor> previousSamples,
                                                   IReadOnlyList<FloatTensor> coarseSlices,
                                                   float t, int seedOffset, float[] weights)
    {
        const int s = LatentTileSize, st = LatentTileStride;
        int plane = s * s;
        int batch = windowIndices.Count;
        float cosT = (float)Math.Cos(t), sinT = (float)Math.Sin(t);

        var xTPerItem = new float[batch][];
        var modelInBatch = new float[batch * 5 * plane];
        var condInputBatch = new float[batch * 58];

        for (int b = 0; b < batch; b++)
        {
            int[] windowIndex = windowIndices[b];
            int i1 = windowIndex[1] * st, j1 = windowIndex[2] * st;

            float[] cond58 = BuildLatentConditioning(coarseSlices[b]);
            Array.Copy(cond58, 0, condInputBatch, b * 58, 58);

            var sample = new float[5 * plane];
            if (previousSamples != null)
            {
                FloatTensor previous = previousSamples[b];
                for (int ch = 0; ch < 5; ch++)
                {
                    for (int px = 0; px < plane; px++)
                    {
                        float w = previous.Data[5 * plane + px];
                        sample[ch * plane + px] = w > 1e-6f ? previous.Data[ch * plane + px] / w * SigmaData : 0f;
                    }
                }
            }

            float[] noise = GaussianNoisePatch.Generate(_seed + (ulong)seedOffset, i1, j1, s, s, 5, s, s);
            var xT = new float[5 * plane];
            for (int k = 0; k < 5 * plane; k++)
            {
                xT[k] = cosT * sample[k] + sinT * (noise[k] * SigmaData);
                modelInBatch[b * 5 * plane + k] = xT[k] / SigmaData;
            }
            xTPerItem[b] = xT;
        }

        var noiseLabels = new float[batch];
        for (int b = 0; b < batch; b++) noiseLabels[b] = t;

        float[] predictionBatch = _baseModel.RunModel(
            modelInBatch, new long[] { batch, 5, s, s },
            noiseLabels, new[] { condInputBatch }, new[] { new long[] { batch, 58 } });

        var results = new List<FloatTensor>(batch);
        for (int b = 0; b < batch; b++)
        {
            float[] xT = xTPerItem[b];
            var output = new FloatTensor(new[] { 6, s, s });
            for (int k = 0; k < 5 * plane; k++)
            {
                // The base model emits the negated prediction.
                float prediction = -predictionBatch[b * 5 * plane + k];
                float newSample = (cosT * xT[k] - sinT * SigmaData * prediction) / SigmaData;
                output.Data[k] = newSample * weights[k % plane];
            }
            Array.Copy(weights, 0, output.Data, 5 * plane, plane);
            results.Add(output);
        }
        return results;
    }

    /// <summary>Builds the 58-value conditioning vector from a (7, 4, 4) coarse slice.</summary>
    private float[] BuildLatentConditioning(FloatTensor coarseSlice)
    {
        const int n = 16;

        var condFlat = new float[6 * n];
        for (int ch = 0; ch < 6; ch++)
        {
            for (int px = 0; px < n; px++)
            {
                float w = coarseSlice.Data[6 * n + px];
                condFlat[ch * n + px] = w > 1e-6f ? coarseSlice.Data[ch * n + px] / w : 0f;
            }
        }

        var condImg7 = new float[7 * n];
        for (int ch = 0; ch < 6; ch++)
        {
            for (int px = 0; px < n; px++)
            {
                float v = (condFlat[ch * n + px] - CondMeans[ch]) / CondStds[ch];
                condImg7[ch * n + px] = float.IsNaN(v) ? 0f : v;
            }
        }
        float maskNorm = (1.0f - CondMeans[6]) / CondStds[6];
        for (int px = 0; px < n; px++) condImg7[6 * n + px] = maskNorm;

        var meansCrop = new float[16];
        Array.Copy(condImg7, 0, meansCrop, 0, 16);
        var p5Crop = new float[16];
        Array.Copy(condImg7, 16, p5Crop, 0, 16);
        var maskCrop = new float[16];
        Array.Copy(condImg7, 6 * 16, maskCrop, 0, 16);

        var climateMeans = new float[4];
        for (int ch = 0; ch < 4; ch++)
        {
            float sum = 0;
            for (int r = 1; r < 3; r++)
                for (int c = 1; c < 3; c++)
                    sum += condImg7[(2 + ch) * 16 + r * 4 + c];
            float mean = sum / 4f;
            climateMeans[ch] = float.IsNaN(mean) ? 0f : mean;
        }

        float noiseLevelNorm = (0f - 0.5f) * (float)Math.Sqrt(12.0);

        var output = new float[58];
        int offset = 0;
        offset = AppendScaled(output, offset, meansCrop, _mpConcatScales[0]);
        offset = AppendScaled(output, offset, p5Crop, _mpConcatScales[1]);
        offset = AppendScaled(output, offset, climateMeans, _mpConcatScales[2]);
        offset = AppendScaled(output, offset, maskCrop, _mpConcatScales[3]);
        offset = AppendScaled(output, offset, _histogramRaw, _mpConcatScales[4]);
        output[offset] = noiseLevelNorm * _mpConcatScales[5];
        return output;
    }

    // =====================================================================
    // Decoder stage
    // =====================================================================

    private InfiniteTensor BuildDecoderStage()
    {
        const int s = DecoderTileSize, st = DecoderTileStride;
        int lc = _latentCompression;
        var outWindow = new TensorWindow(new[] { 2, s, s }, new[] { 2, st, st });
        var inputWindow = new TensorWindow(new[] { 6, s / lc, s / lc }, new[] { 6, st / lc, st / lc });
        float[] weights = LinearWeightWindow(s);
        float t = (float)Math.Atan(EdmScheduler.SigmaMax / SigmaData);

        return _tileStore.GetOrCreate("init_residual_map", new int?[] { 2, null, null },
            (windowIndex, args) => DecoderTile(windowIndex, args[0], t, weights),
            outWindow, new[] { _latents }, new[] { inputWindow }, _cacheLimitBytes);
    }

    private FloatTensor DecoderTile(int[] windowIndex, FloatTensor latentSlice, float t, float[] weights)
    {
        const int s = DecoderTileSize, st = DecoderTileStride;
        int lc = _latentCompression;
        int sl = s / lc;
        int latentPlane = sl * sl;
        int plane = s * s;
        int i1 = windowIndex[1] * st, j1 = windowIndex[2] * st;
        float cosT = (float)Math.Cos(t), sinT = (float)Math.Sin(t);

        var latents = new float[4 * latentPlane];
        for (int ch = 0; ch < 4; ch++)
        {
            for (int px = 0; px < latentPlane; px++)
            {
                float w = latentSlice.Data[5 * latentPlane + px];
                latents[ch * latentPlane + px] = w > 1e-6f ? latentSlice.Data[ch * latentPlane + px] / w : 0f;
            }
        }

        float[] upsampled = NearestUpsample(latents, 4, sl, sl, s, s);

        float[] noise = GaussianNoisePatch.Generate(_seed + 5819, i1, j1, s, s, 1, s, s);
        var xT = new float[plane];
        var modelIn = new float[5 * plane];
        for (int k = 0; k < plane; k++)
        {
            xT[k] = sinT * noise[k] * SigmaData; // the sample starts at zero
            modelIn[k] = xT[k] / SigmaData;
        }
        Array.Copy(upsampled, 0, modelIn, plane, 4 * plane);

        float[] rawPrediction = _decoderModel.RunModel(modelIn, new long[] { 1, 5, s, s }, new[] { t }, null, null);

        var result = new FloatTensor(new[] { 2, s, s });
        for (int k = 0; k < plane; k++)
        {
            // The decoder also emits the negated prediction.
            float prediction = -rawPrediction[k];
            float newSample = (cosT * xT[k] - sinT * SigmaData * prediction) / SigmaData;
            result.Data[k] = newSample * weights[k];
        }
        Array.Copy(weights, 0, result.Data, plane, plane);
        return result;
    }

    // =====================================================================
    // Public sampling API
    // =====================================================================

    /// <summary>
    /// Coarse tensor slice with shape [7, ci1-ci0, cj1-cj0] in coarse index units
    /// (1 unit = 256 native pixels). Channel 6 is the blend weight.
    /// </summary>
    public FloatTensor GetCoarseSlice(int ci0, int cj0, int ci1, int cj1)
        => _coarse.GetSlice(new[] { 0, ci0, cj0 }, new[] { 7, ci1, cj1 });

    /// <summary>Result of sampling the pipeline over a native-pixel window.</summary>
    public readonly struct Sample
    {
        /// <summary>Elevation in metres, row-major (H * W).</summary>
        public readonly float[] Elevation;

        /// <summary>
        /// Climate channels, row-major (5 * H * W): mean temperature (C), temperature seasonality,
        /// annual precipitation (mm), precipitation seasonality, and the local lapse rate (K/m).
        /// Null when climate was not requested.
        /// </summary>
        public readonly float[] Climate;

        public Sample(float[] elevation, float[] climate)
        {
            Elevation = elevation;
            Climate = climate;
        }
    }

    /// <summary>Samples elevation (and optionally climate) over the half-open window [i1,i2) x [j1,j2).</summary>
    public Sample Get(int i1, int j1, int i2, int j2, bool withClimate)
    {
        float[] elevation = ComputeElevation(i1, j1, i2, j2);
        int h = i2 - i1, w = j2 - j1;
        float[] climate = withClimate ? ComputeClimate(i1, j1, i2, j2, elevation, h, w) : null;
        return new Sample(elevation, climate);
    }

    private float[] ComputeElevation(int i1, int j1, int i2, int j2)
    {
        int lc = _latentCompression;
        const float sigma = 5.0f;
        int kernelSize = ((int)(sigma * 2) / 2) * 2 + 1;
        int padLowRes = kernelSize / 2 + 1;
        int padHighRes = padLowRes * lc;

        int pi1 = FloorDiv(i1 - padHighRes, lc) * lc;
        int pj1 = FloorDiv(j1 - padHighRes, lc) * lc;
        int pi2 = -FloorDiv(-(i2 + padHighRes), lc) * lc;
        int pj2 = -FloorDiv(-(j2 + padHighRes), lc) * lc;
        int ph = pi2 - pi1, pw = pj2 - pj1;

        FloatTensor residualSlice = _residual.GetSlice(new[] { 0, pi1, pj1 }, new[] { 2, pi2, pj2 });
        var residual = new float[ph][];
        for (int r = 0; r < ph; r++)
        {
            residual[r] = new float[pw];
            for (int c = 0; c < pw; c++)
            {
                int idx = r * pw + c;
                float w = residualSlice.Data[ph * pw + idx];
                float v = w > 1e-6f ? residualSlice.Data[idx] / w : 0f;
                residual[r][c] = v * _residualStd + _residualMean;
            }
        }

        int lh = ph / lc, lw = pw / lc;
        FloatTensor latentSlice = _latents.GetSlice(
            new[] { 0, pi1 / lc, pj1 / lc }, new[] { 6, pi2 / lc, pj2 / lc });
        var lowFrequency = new float[lh][];
        for (int r = 0; r < lh; r++)
        {
            lowFrequency[r] = new float[lw];
            for (int c = 0; c < lw; c++)
            {
                int idx = r * lw + c;
                float w = latentSlice.Data[5 * lh * lw + idx];
                float v = w > 1e-6f ? latentSlice.Data[4 * lh * lw + idx] / w : 0f;
                lowFrequency[r][c] = v * LowFreqStd + LowFreqMean;
            }
        }

        float[][] newLowRes = LaplacianUtils.LaplacianDenoise(residual, lowFrequency, sigma);
        float[][] elevationPadded = LaplacianUtils.LaplacianDecode(residual, newLowRes);

        int oi = i1 - pi1, oj = j1 - pj1, h = i2 - i1, w2 = j2 - j1;
        var flat = new float[h * w2];
        for (int r = 0; r < h; r++)
        {
            float[] row = elevationPadded[oi + r];
            for (int c = 0; c < w2; c++)
            {
                // Elevation is modelled as a signed square root; undo it here.
                float es = row[oj + c];
                flat[r * w2 + c] = Math.Sign(es) * es * es;
            }
        }
        return flat;
    }

    private float[] ComputeClimate(int i1, int j1, int i2, int j2, float[] elevation, int h, int w)
    {
        int s = 32 * _latentCompression; // native pixels covered by one coarse pixel

        int ci1 = FloorDiv(i1, s);
        int cj1 = FloorDiv(j1, s);
        int ci2 = -FloorDiv(-i2, s);
        int cj2 = -FloorDiv(-j2, s);

        const int window = 15;
        const int pad = (window - 1) / 2 + 1;

        FloatTensor coarseSlice = _coarse.GetSlice(
            new[] { 0, ci1 - pad, cj1 - pad }, new[] { 7, ci2 + pad, cj2 + pad });
        int ch2 = ci2 + pad - (ci1 - pad);
        int cw = cj2 + pad - (cj1 - pad);
        int coarsePlane = ch2 * cw;

        var coarseMap = new float[6][];
        for (int ch = 0; ch < 6; ch++)
        {
            coarseMap[ch] = new float[coarsePlane];
            for (int px = 0; px < coarsePlane; px++)
            {
                float weight = coarseSlice.Data[6 * coarsePlane + px];
                coarseMap[ch][px] = weight > 1e-6f ? coarseSlice.Data[ch * coarsePlane + px] / weight : 0f;
            }
        }

        // Coarse elevation with the sqrt undone; ocean pixels clamp to zero as in Python.
        var coarseElevation = new float[coarsePlane];
        for (int px = 0; px < coarsePlane; px++)
        {
            float v = Math.Max(0f, coarseMap[0][px]);
            coarseElevation[px] = v * v;
        }

        float[][][] lapse = LaplacianUtils.LocalBaselineTemperature(
            To2D(coarseMap[2], ch2, cw), To2D(coarseElevation, ch2, cw), window, 0.02f);
        int lh = lapse[0].Length, lw = lapse[0][0].Length;

        const int centralPad = window / 2;
        int cenH = ch2 - 2 * centralPad, cenW = cw - 2 * centralPad;
        var centralCoarse = new float[6][][];
        for (int ch = 0; ch < 6; ch++)
        {
            centralCoarse[ch] = CropArray(To2D(coarseMap[ch], ch2, cw), centralPad, centralPad, cenH, cenW);
        }

        var climate = new float[5 * h * w];
        int plane = h * w;
        for (int r = 0; r < h; r++)
        {
            float gridY = (i1 + r + 0.5f) / s - ci1 + 0.5f;
            for (int c = 0; c < w; c++)
            {
                float gridX = (j1 + c + 0.5f) / s - cj1 + 0.5f;
                int idx = r * w + c;

                float tBase = BilinearSample2D(lapse[0], lh, lw, gridY, gridX);
                float beta = BilinearSample2D(lapse[1], lh, lw, gridY, gridX);

                climate[idx] = tBase + beta * Math.Max(0f, elevation[idx]);
                climate[plane + idx] = BilinearSample2D(centralCoarse[3], cenH, cenW, gridY, gridX);
                climate[2 * plane + idx] = BilinearSample2D(centralCoarse[4], cenH, cenW, gridY, gridX);
                climate[3 * plane + idx] = BilinearSample2D(centralCoarse[5], cenH, cenW, gridY, gridX);
                climate[4 * plane + idx] = beta;
            }
        }
        return climate;
    }

    // =====================================================================
    // Helpers
    // =====================================================================

    internal static float[] LinearWeightWindow(int size)
    {
        var weights = new float[size * size];
        float mid = (size - 1) / 2.0f;
        const float eps = 1e-3f;
        for (int r = 0; r < size; r++)
        {
            float wy = 1f - (1f - eps) * Math.Min(1f, Math.Abs(r - mid) / mid);
            for (int c = 0; c < size; c++)
            {
                float wx = 1f - (1f - eps) * Math.Min(1f, Math.Abs(c - mid) / mid);
                weights[r * size + c] = wy * wx;
            }
        }
        return weights;
    }

    private static int AppendScaled(float[] output, int offset, float[] values, float scale)
    {
        foreach (float v in values) output[offset++] = v * scale;
        return offset;
    }

    internal static float[] NearestUpsample(float[] source, int channels, int sh, int sw, int dh, int dw)
    {
        var destination = new float[channels * dh * dw];
        for (int c = 0; c < channels; c++)
        {
            for (int r = 0; r < dh; r++)
            {
                int sr = r * sh / dh;
                int dstRow = c * dh * dw + r * dw;
                int srcRow = c * sh * sw + sr * sw;
                for (int col = 0; col < dw; col++)
                    destination[dstRow + col] = source[srcRow + col * sw / dw];
            }
        }
        return destination;
    }

    internal static float[][] To2D(float[] flat, int h, int w)
    {
        var a = new float[h][];
        for (int r = 0; r < h; r++)
        {
            a[r] = new float[w];
            Array.Copy(flat, r * w, a[r], 0, w);
        }
        return a;
    }

    private static float[][] CropArray(float[][] source, int r0, int c0, int h, int w)
    {
        var output = new float[h][];
        for (int r = 0; r < h; r++)
        {
            output[r] = new float[w];
            Array.Copy(source[r + r0], c0, output[r], 0, w);
        }
        return output;
    }

    internal static float BilinearSample2D(float[][] source, int h, int w, float gy, float gx)
    {
        float y = Math.Clamp(gy, 0f, h - 1f);
        float x = Math.Clamp(gx, 0f, w - 1f);
        int y0 = (int)y, y1 = Math.Min(h - 1, y0 + 1);
        int x0 = (int)x, x1 = Math.Min(w - 1, x0 + 1);
        float wy = y - y0, wx = x - x0;
        return (1 - wy) * (1 - wx) * source[y0][x0] + (1 - wy) * wx * source[y0][x1]
             + wy * (1 - wx) * source[y1][x0] + wy * wx * source[y1][x1];
    }

    internal static int FloorDiv(int a, int b)
    {
        int q = a / b;
        if ((a % b != 0) && ((a < 0) != (b < 0))) q--;
        return q;
    }
}
