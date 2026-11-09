using System;
using System.Collections.Generic;
using VSTerrainDiffusion.Core;
using VSTerrainDiffusion.Tensors;
using static VSTerrainDiffusion.Core.IntMath;
using static VSTerrainDiffusion.Pipeline.Raster;

namespace VSTerrainDiffusion.Pipeline;

/// <summary>
/// What the rest of the mod asks of the pipeline: elevation and climate over a window of native
/// pixels, and the coarse map itself.
/// </summary>
public sealed partial class WorldPipeline
{
    /// <summary>Structure sites' basin depth over a factory window (x along J, y along I).</summary>
    private float[] SiteBasins(int x1, int y1, int x2, int y2) => CoarseConditioningSite.BasinDepths(_sites, x1, y1, x2, y2);

    /// <summary>
    /// The conditioning the coarse stage is handed at one cell, in real units and before it is
    /// normalised and stirred into noise: [elevation m, temperature C, BIO4, precipitation mm,
    /// BIO15]. For the debug map, so what the model was asked for can be read against what it
    /// produced.
    ///
    /// Coordinates are swapped on the way in, the same way <see cref="CoarseTile"/> swaps them.
    /// </summary>
    public float[] CoarseConditioningAt(int i, int j)
    {
        // The conditioning is in the coarse model's own grid, n times finer than the world's coarse
        // cells under pooling; read the pixel at the middle of this cell.
        int n = _pooling.Factor;
        int ci = i * n + n / 2, cj = j * n + n / 2;
        float[] synthetic = _syntheticMapFactory.Sample(cj, ci, cj + 1, ci + 1);

        // Channel 0 arrives in signed square-root space, which is not a number anyone can read.
        float e = synthetic[0];
        synthetic[0] = Math.Sign(e) * e * e;
        return synthetic;
    }

    /// <summary>How much of one coarse cell the world's ocean map calls sea, or null without one.</summary>
    public float? SeaFractionAt(int i, int j)
    {
        int n = _pooling.Factor;
        float[] sea = _landmask?.SeaFraction(j * n, i * n, (j + 1) * n, (i + 1) * n);
        if (sea is not { Length: > 0 }) return null;

        float sum = 0f;
        foreach (float v in sea) sum += v;
        return sum / sea.Length;
    }

    /// <summary>
    /// The coarse map over [ci0, ci1) x [cj0, cj1): <see cref="CoarseSpec"/>'s channels, each
    /// multiplied by the blend weight, then the weight itself at <see cref="CoarseModelSpec.WeightChannel"/>.
    /// </summary>
    public FloatTensor GetCoarseSlice(int ci0, int cj0, int ci1, int cj1)
    {
        FloatTensor slice = _coarse.GetSlice(new[] { 0, ci0, cj0 }, new[] { _coarseChannels + 1, ci1, cj1 });
        CoarseConditioningSite.ApplyClimateToSlice(_sites, slice.Data, _coarseChannels, ci0, cj0, ci1 - ci0, cj1 - cj0, _pooling.Factor);
        return slice;
    }

    /// <summary>Result of sampling the pipeline over a native-pixel window.</summary>
    public readonly struct Sample
    {
        /// <summary>Elevation in metres, row-major (H * W).</summary>
        public readonly float[] Elevation;

        /// <summary>
        /// Climate channels, row-major (planes * H * W), indexed by <see cref="ClimatePlane"/>: mean
        /// temperature (C), temperature seasonality, annual precipitation (mm), precipitation
        /// seasonality, the local lapse rate (K/m), and for a seasonal coarse model the six cycle
        /// channels and the diurnal range. Null when climate was not requested.
        /// </summary>
        public readonly float[] Climate;

        /// <summary>Creates a sample.</summary>
        /// <param name="elevation">Elevation, metres, row-major.</param>
        /// <param name="climate">Climate channels, or null.</param>
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

    /// <summary>
    /// Elevation over a window: the decoder's residual plus the latents' low-frequency channel, smoothed and
    /// upsampled, then decoded from the model's signed square-root scale to metres.
    /// </summary>
    /// <param name="i1">First row.</param>
    /// <param name="j1">First column.</param>
    /// <param name="i2">Row after the last.</param>
    /// <param name="j2">Column after the last.</param>
    /// <returns>Metres, row-major.</returns>
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

    /// <summary>
    /// Climate over a window, from the coarse map: temperature corrected to the window's own elevation with a
    /// locally fitted lapse rate, the other channels interpolated.
    /// </summary>
    /// <param name="i1">First row.</param>
    /// <param name="j1">First column.</param>
    /// <param name="i2">Row after the last.</param>
    /// <param name="j2">Column after the last.</param>
    /// <param name="elevation">The window's elevation, from <see cref="ComputeElevation"/>.</param>
    /// <param name="h">Rows.</param>
    /// <param name="w">Columns.</param>
    /// <returns>The climate channels, channel-major.</returns>
    private float[] ComputeClimate(int i1, int j1, int i2, int j2, float[] elevation, int h, int w)
    {
        int s = 32 * _latentCompression; // native pixels covered by one coarse pixel

        int ci1 = FloorDiv(i1, s);
        int cj1 = FloorDiv(j1, s);
        int ci2 = -FloorDiv(-i2, s);
        int cj2 = -FloorDiv(-j2, s);

        const int window = 15;
        const int pad = (window - 1) / 2 + 1;

        int channels = _coarseChannels;

        // Fetched wider by the coast fill's reach, so every cell kept below sees all the land the
        // fill looks at whichever tile asks, and then cropped back.
        const int margin = CoastFill.Radius;
        FloatTensor coarseSlice = _coarse.GetSlice(
            new[] { 0, ci1 - pad - margin, cj1 - pad - margin },
            new[] { channels + 1, ci2 + pad + margin, cj2 + pad + margin });
        int fh = ci2 - ci1 + 2 * (pad + margin), fw = cj2 - cj1 + 2 * (pad + margin);
        int fetchedPlane = fh * fw;
        var fetched = new float[channels][];
        for (int ch = 0; ch < channels; ch++)
        {
            fetched[ch] = new float[fetchedPlane];
            for (int px = 0; px < fetchedPlane; px++)
            {
                float weight = coarseSlice.Data[channels * fetchedPlane + px];
                fetched[ch][px] = weight > 1e-6f ? coarseSlice.Data[ch * fetchedPlane + px] / weight : 0f;
            }
        }

        // The model's climate over the sea is filler; give the sea its shore's climate before any
        // of it is spread onto land (see CoastFill).
        if (_coastFill) CoastFill.Apply(fetched, fh, fw);

        int ch2 = ci2 + pad - (ci1 - pad);
        int cw = cj2 + pad - (cj1 - pad);
        int coarsePlane = ch2 * cw;
        var coarseMap = new float[channels][];
        for (int ch = 0; ch < channels; ch++)
        {
            coarseMap[ch] = new float[coarsePlane];
            for (int r = 0; r < ch2; r++)
                Array.Copy(fetched[ch], (r + margin) * fw + margin, coarseMap[ch], r * cw, cw);
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
        var centralCoarse = new float[channels][][];
        for (int ch = 0; ch < channels; ch++)
        {
            centralCoarse[ch] = CropArray(To2D(coarseMap[ch], ch2, cw), centralPad, centralPad, cenH, cenW);
        }

        int planes = ClimatePlane.CountFor(_coarseSpec);
        var climate = new float[planes * h * w];

        // Story structure climates, per column; in coarse model pixels, a native pixel is n / s.
        float toModel = (float)_pooling.Factor / s;
        List<CoarseConditioningSite> sites = CoarseConditioningSite.ClimateSitesNear(
            _sites, i1 * toModel, j1 * toModel, (i1 + h) * toModel, (j1 + w) * toModel);
        int plane = h * w;
        for (int r = 0; r < h; r++)
        {
            float gridY = (i1 + r + 0.5f) / s - ci1 + 0.5f;
            for (int c = 0; c < w; c++)
            {
                float gridX = (j1 + c + 0.5f) / s - cj1 + 0.5f;
                int idx = r * w + c;

                float tBase = BilinearSample2D(lapse[0], lh, lw, gridY, gridX);
                // Altitude cooling adds to the model's own fitted rate rather than scaling it: that
                // rate is often shallow, a few degrees a kilometre, and tripling it barely showed,
                // while the air above the ground cooled at the game-side reference rate times the
                // setting. Extra cooling at the reference rate keeps the two the same.
                float beta = BilinearSample2D(lapse[1], lh, lw, gridY, gridX)
                             - (_altitudeCooling - 1f) * ClimateScale.ReferenceLapseCPerKm / 1000f;

                climate[idx] = tBase + beta * Math.Max(0f, elevation[idx]);
                climate[plane + idx] = BilinearSample2D(centralCoarse[3], cenH, cenW, gridY, gridX);
                climate[2 * plane + idx] = BilinearSample2D(centralCoarse[4], cenH, cenW, gridY, gridX);
                climate[3 * plane + idx] = BilinearSample2D(centralCoarse[5], cenH, cenW, gridY, gridX);
                climate[4 * plane + idx] = beta;
                if (sites != null)
                {
                    float coarseTemperature = BilinearSample2D(centralCoarse[2], cenH, cenW, gridY, gridX);
                    CoarseConditioningSite.BlendColumn(sites, (i1 + r + 0.5f) * toModel, (j1 + c + 0.5f) * toModel,
                        coarseTemperature, climate, plane, idx);
                }

                // The cycle channels and the diurnal range are as smooth as the coarse map; only
                // temperature has detail at this resolution.
                for (int k = ClimatePlane.TemperatureShapeCos; k < planes; k++)
                {
                    int coarseChannel = k - ClimatePlane.TemperatureShapeCos + CoarseChannel.TemperatureShapeCos;
                    climate[k * plane + idx] = BilinearSample2D(centralCoarse[coarseChannel], cenH, cenW, gridY, gridX);
                }
            }
        }
        return climate;
    }
}
