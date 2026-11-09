using System;

namespace VSTerrainDiffusion.Pipeline;

/// <summary>Small raster operations the pipeline stages share: blend windows, resampling, row arrays.</summary>
internal static class Raster
{
    /// <summary>
    /// The blend weights of a square window: a tent falling linearly from 1 at the centre to 1e-3 at the
    /// edges, so overlapping windows cross-fade.
    /// </summary>
    /// <param name="size">Edge length.</param>
    /// <returns>size x size weights, row-major.</returns>
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

    /// <summary>Nearest-neighbour upsample of a channel-major raster into a new array.</summary>
    /// <param name="source">The raster, channels x sh x sw.</param>
    /// <param name="channels">Channel count.</param>
    /// <param name="sh">Source height.</param>
    /// <param name="sw">Source width.</param>
    /// <param name="dh">Destination height.</param>
    /// <param name="dw">Destination width.</param>
    /// <returns>The raster, channels x dh x dw.</returns>
    internal static float[] NearestUpsample(float[] source, int channels, int sh, int sw, int dh, int dw)
    {
        var destination = new float[channels * dh * dw];
        NearestUpsampleInto(source, channels, sh, sw, dh, dw, destination, 0);
        return destination;
    }

    /// <summary>
    /// Nearest-neighbour upsample written straight into <paramref name="destination"/> at
    /// <paramref name="offset"/>. The column mapping repeats for every row, so it is computed once
    /// and reused rather than divided per pixel.
    /// </summary>
    internal static void NearestUpsampleInto(float[] source, int channels, int sh, int sw,
                                             int dh, int dw, float[] destination, int offset)
    {
        var columnMap = new int[dw];
        for (int col = 0; col < dw; col++) columnMap[col] = col * sw / dw;

        for (int c = 0; c < channels; c++)
        {
            for (int r = 0; r < dh; r++)
            {
                int sr = r * sh / dh;
                int dstRow = offset + c * dh * dw + r * dw;
                int srcRow = c * sh * sw + sr * sw;
                for (int col = 0; col < dw; col++)
                    destination[dstRow + col] = source[srcRow + columnMap[col]];
            }
        }
    }

    /// <summary>Copies a flat h x w array into one array per row.</summary>
    /// <param name="flat">The values, row-major.</param>
    /// <param name="h">Rows.</param>
    /// <param name="w">Columns.</param>
    /// <returns>The rows.</returns>
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

    /// <summary>Copies an h x w block out of a row array.</summary>
    /// <param name="source">The rows.</param>
    /// <param name="r0">First row.</param>
    /// <param name="c0">First column.</param>
    /// <param name="h">Rows to copy.</param>
    /// <param name="w">Columns to copy.</param>
    /// <returns>The block's rows.</returns>
    internal static float[][] CropArray(float[][] source, int r0, int c0, int h, int w)
    {
        var output = new float[h][];
        for (int r = 0; r < h; r++)
        {
            output[r] = new float[w];
            Array.Copy(source[r + r0], c0, output[r], 0, w);
        }
        return output;
    }

    /// <summary>Bilinear sample of a row array at a fractional position, clamped to its edges.</summary>
    /// <param name="source">The rows.</param>
    /// <param name="h">Rows.</param>
    /// <param name="w">Columns.</param>
    /// <param name="gy">Row position.</param>
    /// <param name="gx">Column position.</param>
    /// <returns>The interpolated value.</returns>
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
}
