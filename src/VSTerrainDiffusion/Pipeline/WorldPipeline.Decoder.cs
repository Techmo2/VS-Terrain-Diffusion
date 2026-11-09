using System;
using VSTerrainDiffusion.Tensors;
using static VSTerrainDiffusion.Pipeline.Raster;

namespace VSTerrainDiffusion.Pipeline;

/// <summary>
/// The decoder stage: one flow-matching step from the latents to the high-frequency elevation
/// residual at native resolution.
/// </summary>
public sealed partial class WorldPipeline
{
    /// <summary>
    /// Registers the residual map: 256-pixel windows at a stride of 192, one decoder step from the latents.
    /// </summary>
    /// <returns>The residual map.</returns>
    private InfiniteTensor BuildDecoderStage()
    {
        const int s = DecoderTileSize, st = DecoderTileStride;
        int lc = _latentCompression;
        var outWindow = new TensorWindow(new[] { 2, s, s }, new[] { 2, st, st });
        var inputWindow = new TensorWindow(new[] { 6, s / lc, s / lc }, new[] { 6, st / lc, st / lc });
        float[] weights = LinearWeightWindow(s);
        float t = (float)Math.Atan(EdmScheduler.SigmaMax / SigmaData);

        // Deliberately not batched. The decoder is the widest graph in the pipeline and already
        // saturates the GPU with one 256x256 window: batching four of them measured 5% slower for
        // the same work, because there is no launch overhead left to hide.
        return _tileStore.GetOrCreate("init_residual_map", new int?[] { 2, null, null },
            (windowIndex, args) => DecoderTile(windowIndex, args[0], t, weights),
            outWindow, new[] { _latents }, new[] { inputWindow });
    }

    /// <summary>Runs the decoder over one window.</summary>
    /// <param name="windowIndex">The window's index (channel, I, J).</param>
    /// <param name="latentSlice">The latents under the window.</param>
    /// <param name="t">The step's noise angle.</param>
    /// <param name="weights">The blend window.</param>
    /// <returns>The window: the residual then the weight.</returns>
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

        float[] noise = GaussianNoisePatch.Generate(_seed + 5819, i1, j1, s, s, 1, s, s);
        var xT = new float[plane];
        var modelIn = new float[5 * plane];
        for (int k = 0; k < plane; k++)
        {
            xT[k] = sinT * noise[k] * SigmaData; // the sample starts at zero
            modelIn[k] = xT[k] / SigmaData;
        }

        // Straight into the model input rather than through a 4 x 256 x 256 staging buffer: that is
        // a megabyte of copying for every window the decoder produces.
        NearestUpsampleInto(latents, 4, sl, sl, s, s, modelIn, plane);

        InferencePreemption.ThrowIfPreempted();
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
}
