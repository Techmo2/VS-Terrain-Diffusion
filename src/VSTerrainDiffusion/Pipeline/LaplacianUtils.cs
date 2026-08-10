using System;

namespace VSTerrainDiffusion.Pipeline;

/// <summary>
/// Port of terrain_diffusion/data/laplacian_encoder.py: the decode/denoise steps used by
/// <see cref="WorldPipeline"/> when turning latents plus residual into elevation, plus the
/// windowed lapse-rate regression from postprocessing.py.
/// </summary>
public static class LaplacianUtils
{
    /// <summary>laplacian_decode: bilinearly upsample lowres to the residual size and add it.</summary>
    public static float[][] LaplacianDecode(float[][] residual, float[][] lowres)
    {
        int h = residual.Length, w = residual[0].Length;
        float[][] lowresUp = BilinearResize(lowres, h, w);
        var result = new float[h][];
        for (int r = 0; r < h; r++)
        {
            result[r] = new float[w];
            for (int c = 0; c < w; c++) result[r][c] = residual[r][c] + lowresUp[r][c];
        }
        return result;
    }

    /// <summary>
    /// laplacian_denoise(residual, lowres, sigma) with extrapolate=True: decode with linear edge
    /// extrapolation, then re-encode by downsampling and blurring. Returns the new lowres.
    /// </summary>
    public static float[][] LaplacianDenoise(float[][] residual, float[][] lowres, float sigma)
    {
        int h = residual.Length, w = residual[0].Length;
        int lh = lowres.Length, lw = lowres[0].Length;

        float[][] lowresUpEx = BilinearResizeExtrapolated(lowres, h, w);
        var decoded = new float[h][];
        for (int r = 0; r < h; r++)
        {
            decoded[r] = new float[w];
            for (int c = 0; c < w; c++) decoded[r][c] = residual[r][c] + lowresUpEx[r][c];
        }

        float[][] downsampled = BilinearResize(decoded, lh, lw);
        return SeparableGaussianBlur(downsampled, GaussianKernel1D(sigma));
    }

    /// <summary>Bilinear resize with align_corners=False, matching PyTorch's interpolate.</summary>
    public static float[][] BilinearResize(float[][] src, int dstH, int dstW)
    {
        int srcH = src.Length, srcW = src[0].Length;
        var dst = new float[dstH][];
        for (int r = 0; r < dstH; r++)
        {
            dst[r] = new float[dstW];
            float srcR = ((r + 0.5f) * srcH / dstH) - 0.5f;
            int r0 = (int)Math.Floor(srcR);
            int r1 = r0 + 1;
            float wr = srcR - r0;
            r0 = Math.Clamp(r0, 0, srcH - 1);
            r1 = Math.Clamp(r1, 0, srcH - 1);
            float[] row0 = src[r0], row1 = src[r1];

            for (int c = 0; c < dstW; c++)
            {
                float srcC = ((c + 0.5f) * srcW / dstW) - 0.5f;
                int c0 = (int)Math.Floor(srcC);
                int c1 = c0 + 1;
                float wc = srcC - c0;
                c0 = Math.Clamp(c0, 0, srcW - 1);
                c1 = Math.Clamp(c1, 0, srcW - 1);
                dst[r][c] = (1 - wr) * (1 - wc) * row0[c0]
                          + (1 - wr) * wc * row0[c1]
                          + wr * (1 - wc) * row1[c0]
                          + wr * wc * row1[c1];
            }
        }
        return dst;
    }

    /// <summary>
    /// Bilinear resize with a one-pixel linearly extrapolated border, used by
    /// <see cref="LaplacianDenoise"/> (extrapolate=True).
    /// </summary>
    internal static float[][] BilinearResizeExtrapolated(float[][] src, int dstH, int dstW)
    {
        int sH = src.Length, sW = src[0].Length;
        var padded = new float[sH + 2][];
        for (int r = 0; r < sH + 2; r++) padded[r] = new float[sW + 2];

        for (int r = 0; r < sH; r++)
            Array.Copy(src[r], 0, padded[r + 1], 1, sW);

        for (int c = 1; c <= sW; c++)
        {
            padded[0][c] = sH > 1 ? 2 * src[0][c - 1] - src[1][c - 1] : src[0][c - 1];
            padded[sH + 1][c] = sH > 1 ? 2 * src[sH - 1][c - 1] - src[sH - 2][c - 1] : src[sH - 1][c - 1];
        }
        for (int r = 0; r < sH + 2; r++)
        {
            padded[r][0] = sW > 1 ? 2 * padded[r][1] - padded[r][2] : padded[r][1];
            padded[r][sW + 1] = sW > 1 ? 2 * padded[r][sW] - padded[r][sW - 1] : padded[r][sW];
        }

        int newH = (int)Math.Round(dstH + 2.0 * dstH / sH, MidpointRounding.AwayFromZero);
        int newW = (int)Math.Round(dstW + 2.0 * dstW / sW, MidpointRounding.AwayFromZero);
        float[][] resized = BilinearResize(padded, newH, newW);

        int padH = (int)Math.Round((double)dstH / sH, MidpointRounding.AwayFromZero);
        int padW = (int)Math.Round((double)dstW / sW, MidpointRounding.AwayFromZero);
        var cropped = new float[dstH][];
        for (int r = 0; r < dstH; r++)
        {
            cropped[r] = new float[dstW];
            Array.Copy(resized[r + padH], padW, cropped[r], 0, dstW);
        }
        return cropped;
    }

    /// <summary>Builds the 1D Gaussian kernel used for the separable blur.</summary>
    public static float[] GaussianKernel1D(float sigma)
    {
        int ks = ((int)(sigma * 2) / 2) * 2 + 1; // matches PyTorch gaussian_blur
        var k = new float[ks];
        float sum = 0;
        int half = ks / 2;
        for (int i = 0; i < ks; i++)
        {
            float x = i - half;
            k[i] = (float)Math.Exp(-0.5 * x * x / (sigma * sigma));
            sum += k[i];
        }
        for (int i = 0; i < ks; i++) k[i] /= sum;
        return k;
    }

    /// <summary>Separable Gaussian blur with clamped (replicate) edges.</summary>
    public static float[][] SeparableGaussianBlur(float[][] src, float[] kernel1D)
    {
        int ks = kernel1D.Length;
        int pad = ks / 2;
        int h = src.Length, w = src[0].Length;

        var tmp = new float[h][];
        for (int r = 0; r < h; r++)
        {
            tmp[r] = new float[w];
            float[] srcRow = src[r];
            for (int c = 0; c < w; c++)
            {
                float sum = 0;
                for (int ki = 0; ki < ks; ki++) sum += srcRow[Math.Clamp(c + ki - pad, 0, w - 1)] * kernel1D[ki];
                tmp[r][c] = sum;
            }
        }

        var result = new float[h][];
        for (int r = 0; r < h; r++)
        {
            result[r] = new float[w];
            for (int c = 0; c < w; c++)
            {
                float sum = 0;
                for (int ki = 0; ki < ks; ki++) sum += tmp[Math.Clamp(r + ki - pad, 0, h - 1)][c] * kernel1D[ki];
                result[r][c] = sum;
            }
        }
        return result;
    }

    /// <summary>
    /// Windowed weighted linear regression of temperature on elevation to estimate the local lapse
    /// rate. Port of postprocessing.local_baseline_temperature_torch.
    /// </summary>
    /// <returns>[0] = sea-level baseline temperature, [1] = beta (K per metre), each (H-win+1, W-win+1).</returns>
    public static float[][][] LocalBaselineTemperature(float[][] t, float[][] e, int win, float fallbackThreshold)
    {
        int h = t.Length, w = t[0].Length;
        int outH = h - win + 1, outW = w - win + 1;
        var result = new float[2][][];
        result[0] = new float[outH][];
        result[1] = new float[outH][];

        const float fallbackBeta = -0.0065f;
        const float betaMin = -0.012f, betaMax = 0.0f;
        const float eps = 1e-6f;
        int n = win * win;
        int pad = (win - 1) / 2;

        for (int r = 0; r < outH; r++)
        {
            result[0][r] = new float[outW];
            result[1][r] = new float[outW];
            for (int c = 0; c < outW; c++)
            {
                double muT = 0, muE = 0, muE2 = 0, muEt = 0, sumW = 0;
                for (int dr = 0; dr < win; dr++)
                {
                    float[] tRow = t[r + dr];
                    float[] eRow = e[r + dr];
                    for (int dc = 0; dc < win; dc++)
                    {
                        float ev = eRow[c + dc];
                        float land = ev > 0 ? 1.0f : 0.0f;
                        float tv = tRow[c + dc];
                        muT += tv * land;
                        muE += ev * land;
                        muE2 += ev * ev * land;
                        muEt += ev * tv * land;
                        sumW += land;
                    }
                }

                double den = sumW + eps;
                muT /= den; muE /= den; muE2 /= den; muEt /= den;
                double varE = muE2 - muE * muE;
                double covEt = muEt - muE * muT;
                double beta = (varE < 1.0 || sumW < fallbackThreshold * n) ? fallbackBeta : covEt / (varE + eps);
                beta = Math.Clamp(beta, betaMin, betaMax);

                float tc = t[r + pad][c + pad];
                float ec = e[r + pad][c + pad];
                result[0][r][c] = (float)(tc - beta * ec);
                result[1][r][c] = (float)beta;
            }
        }
        return result;
    }
}
