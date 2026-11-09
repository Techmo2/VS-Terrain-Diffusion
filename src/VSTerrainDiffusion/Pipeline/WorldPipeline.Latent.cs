using System;
using System.Collections.Generic;
using VSTerrainDiffusion.Tensors;
using static VSTerrainDiffusion.Pipeline.Raster;

namespace VSTerrainDiffusion.Pipeline;

/// <summary>
/// The latent stage: the base model's two flow-matching steps, conditioned on the coarse map, giving
/// the decoder's latents.
/// </summary>
public sealed partial class WorldPipeline
{
    /// <summary>
    /// Registers the latent maps: a first step from noise and a second from the first, re-noised to
    /// <see cref="_renoiseT"/>, both over 64-pixel windows at a stride of 32.
    /// </summary>
    /// <returns>The second step's map, which the decoder reads.</returns>
    private InfiniteTensor BuildLatentStage()
    {
        const int s = LatentTileSize, st = LatentTileStride;
        var outWindow = new TensorWindow(new[] { 6, s, s }, new[] { 6, st, st });
        int mapChannels = _coarseChannels + 1;
        var coarseWindow = new TensorWindow(new[] { mapChannels, 4, 4 }, new[] { mapChannels, 1, 1 }, new[] { 0, -1, -1 });
        float[] weights = LinearWeightWindow(s);

        float initialT = (float)Math.Atan(EdmScheduler.SigmaMax / SigmaData);
        InfiniteTensor initialLatent = _tileStore.GetOrCreateBatched(
            "init_latent_map", new int?[] { 6, null, null },
            (windowIndices, args) => LatentBatch(windowIndices, null, args[0], initialT, 5819, weights),
            outWindow, new[] { _coarse }, new[] { coarseWindow }, _latentBatchSize);

        float intermediateT = _renoiseT;
        return _tileStore.GetOrCreateBatched(
            "step_latent_map_0", new int?[] { 6, null, null },
            (windowIndices, args) => LatentBatch(windowIndices, args[0], args[1], intermediateT, 5820, weights),
            outWindow, new[] { initialLatent, _coarse }, new[] { outWindow, coarseWindow }, _latentBatchSize);
    }

    /// <summary>Runs one base-model step over a batch of latent windows.</summary>
    /// <param name="windowIndices">The windows.</param>
    /// <param name="previousSamples">The previous step's windows, or null for the first step.</param>
    /// <param name="coarseSlices">The 4x4 coarse cells around each window.</param>
    /// <param name="t">The step's noise angle.</param>
    /// <param name="seedOffset">Separates the steps' noise streams.</param>
    /// <param name="weights">The blend window.</param>
    /// <returns>One window per input: five latent channels then the weight.</returns>
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

            // The coarse slice starts one map cell before the window (coarseWindow's offset).
            CoarseConditioningSite.ApplyClimateToSlice(_sites, coarseSlices[b].Data, _coarseChannels,
                windowIndex[1] - 1, windowIndex[2] - 1, 4, 4, _pooling.Factor);
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

        InferencePreemption.ThrowIfPreempted();
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

    /// <summary>
    /// Builds the 58-value conditioning vector from a (channels + 1, 4, 4) coarse slice. Only the
    /// annual channels reach the base model, whatever else the coarse model produced.
    /// </summary>
    private float[] BuildLatentConditioning(FloatTensor coarseSlice)
    {
        const int n = 16;

        var condFlat = new float[6 * n];
        for (int ch = 0; ch < 6; ch++)
        {
            for (int px = 0; px < n; px++)
            {
                float w = coarseSlice.Data[_coarseChannels * n + px];
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

        // The conditioning is exact, so noise level 0. The model was trained to accept noisier
        // conditioning at higher levels, but only up to 0.1 in these units - 0.005 of a standard
        // deviation - so raising it changes the terrain by about 3 m (measured) and buys nothing.
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

    /// <summary>Appends scaled values to the conditioning vector.</summary>
    /// <param name="output">The vector.</param>
    /// <param name="offset">Where to write.</param>
    /// <param name="values">The values.</param>
    /// <param name="scale">The factor.</param>
    /// <returns>The offset after them.</returns>
    private static int AppendScaled(float[] output, int offset, float[] values, float scale)
    {
        foreach (float v in values) output[offset++] = v * scale;
        return offset;
    }
}
