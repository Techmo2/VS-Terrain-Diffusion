using System;
using VSTerrainDiffusion.Pipeline;

namespace VSTerrainDiffusion.WorldGen;

/// <summary>
/// Raster operations that turn the pipeline's native-resolution output into a block-resolution
/// terrain tile: reshaping, upsampling, cropping, slope and slope detail. All arrays are row-major
/// with rows along Z.
/// </summary>
public static class TileRaster
{
    /// <summary>
    /// Climate planes carried to block resolution: mean temperature, temperature seasonality,
    /// precipitation, precipitation seasonality and the local lapse rate (degrees per metre), which
    /// moves the temperature with the slope detail added after the model.
    /// </summary>
    public const int ClimateChannels = 5;

    /// <summary>Kilometre-scale octaves of slope detail.</summary>
    private static readonly FastNoiseLite ElevationNoiseCoarse = MakeNoise(99999, 1f / 24f, 3, 2f, 0.5f);

    /// <summary>Block-scale octaves of slope detail.</summary>
    private static readonly FastNoiseLite ElevationNoiseFine = MakeNoise(88888, 1f / 6f, 2, 2f, 0.6f);

    /// <summary>A fractal Perlin generator. Fixed seeds: the detail is the same in every world.</summary>
    private static FastNoiseLite MakeNoise(int seed, float frequency, int octaves, float lacunarity, float gain)
    {
        var fnl = new FastNoiseLite(seed);
        fnl.SetNoiseType(FastNoiseLite.NoiseType.Perlin);
        fnl.SetFrequency(frequency);
        fnl.SetFractalType(FastNoiseLite.FractalType.FBm);
        fnl.SetFractalOctaves(octaves);
        fnl.SetFractalLacunarity(lacunarity);
        fnl.SetFractalGain(gain);
        return fnl;
    }

    /// <summary>
    /// Adds two octaves of Perlin detail on sloped land. The model resolves features down to one
    /// native pixel, so without this, hillsides upsampled to block resolution look glassy.
    /// </summary>
    /// <param name="elevation">size x size elevations in metres, changed in place; sea (below 0) is left alone.</param>
    /// <param name="slope">size x size slopes as rise over run.</param>
    /// <param name="blockX">Model-relative block X of the first column, the noise coordinate.</param>
    /// <param name="blockZ">Model-relative block Z of the first row.</param>
    /// <param name="size">Edge length of the arrays.</param>
    /// <param name="pixelSizeMeters">Metres per block horizontally.</param>
    /// <param name="nativeResolution">Metres per native model pixel.</param>
    /// <param name="detailStrength">Scale of the detail; 0 or less adds none.</param>
    public static void AddSlopeNoise(float[] elevation, float[] slope, int blockX, int blockZ,
                                     int size, float pixelSizeMeters, float nativeResolution,
                                     float detailStrength)
    {
        if (detailStrength <= 0f) return;

        float normFactor = 40f * pixelSizeMeters / nativeResolution;
        float amplitudeCoarse = 100f * pixelSizeMeters / nativeResolution * detailStrength;
        float amplitudeFine = 70f * pixelSizeMeters / nativeResolution * detailStrength;

        for (int r = 0; r < size; r++)
        {
            for (int c = 0; c < size; c++)
            {
                int idx = r * size + c;
                float e = elevation[idx];
                if (e < 0f) continue;

                // slope[] is a ratio; the original pipeline works with the raw gradient here.
                float gradient = slope[idx] * pixelSizeMeters;
                float strength = Math.Min(1f, gradient / normFactor);
                strength = strength * strength * (float)Math.Sqrt(strength);

                float nx = blockX + c, ny = blockZ + r;
                elevation[idx] = e
                    + ElevationNoiseCoarse.GetNoise(nx, ny) * amplitudeCoarse * strength
                    + ElevationNoiseFine.GetNoise(nx, ny) * amplitudeFine * strength;
            }
        }
    }

    /// <summary>Slope (rise over run) of every cell, by the 3x3 Sobel operator.</summary>
    /// <param name="padded">(h + 2) x (w + 2) elevations in metres: the area with a one-cell border.</param>
    /// <param name="h">Rows of the result.</param>
    /// <param name="w">Columns of the result.</param>
    /// <param name="pixelSizeMeters">Metres between neighbouring cells.</param>
    /// <returns>h x w slopes.</returns>
    public static float[] SobelSlope(float[] padded, int h, int w, float pixelSizeMeters)
    {
        int pw = w + 2;
        var slope = new float[h * w];
        for (int r = 0; r < h; r++)
        {
            for (int c = 0; c < w; c++)
            {
                float tl = padded[r * pw + c], tc = padded[r * pw + c + 1], tr = padded[r * pw + c + 2];
                float ml = padded[(r + 1) * pw + c], mr = padded[(r + 1) * pw + c + 2];
                float bl = padded[(r + 2) * pw + c], bc = padded[(r + 2) * pw + c + 1], br = padded[(r + 2) * pw + c + 2];

                float dx = (-tl + tr - 2 * ml + 2 * mr - bl + br) / 8f;
                float dy = (-tl - 2 * tc - tr + bl + 2 * bc + br) / 8f;
                slope[r * w + c] = (float)Math.Sqrt(dx * dx + dy * dy) / pixelSizeMeters;
            }
        }
        return slope;
    }

    /// <summary>Copies a flat h x w array into one array per row.</summary>
    public static float[][] To2D(float[] flat, int h, int w)
    {
        var rows = new float[h][];
        for (int r = 0; r < h; r++)
        {
            rows[r] = new float[w];
            Array.Copy(flat, r * w, rows[r], 0, w);
        }
        return rows;
    }

    /// <summary>
    /// Bilinearly upsamples each of the <see cref="ClimateChannels"/> native climate planes and crops
    /// the tile out of it, as <see cref="CropFlat"/> does for elevation.
    /// </summary>
    /// <param name="climateNative">Channel-major planes of nh x nw native pixels, or null.</param>
    /// <param name="nh">Native rows.</param>
    /// <param name="nw">Native columns.</param>
    /// <param name="cropI">First upsampled row of the tile.</param>
    /// <param name="cropJ">First upsampled column of the tile.</param>
    /// <param name="h">Tile rows.</param>
    /// <param name="w">Tile columns.</param>
    /// <param name="upH">Upsampled rows.</param>
    /// <param name="upW">Upsampled columns.</param>
    /// <returns>Channel-major h x w planes, or null when <paramref name="climateNative"/> is.</returns>
    public static float[] UpsampleClimate(float[] climateNative, int nh, int nw,
                                          int cropI, int cropJ, int h, int w, int upH, int upW)
    {
        if (climateNative == null) return null;

        var result = new float[ClimateChannels * h * w];
        int nativePlane = nh * nw;
        for (int ch = 0; ch < ClimateChannels; ch++)
        {
            var native = new float[nh][];
            for (int r = 0; r < nh; r++)
            {
                native[r] = new float[nw];
                Array.Copy(climateNative, ch * nativePlane + r * nw, native[r], 0, nw);
            }
            float[][] up = LaplacianUtils.BilinearResize(native, upH, upW);
            for (int r = 0; r < h; r++)
            {
                float[] row = up[Math.Clamp(cropI + r, 0, upH - 1)];
                for (int c = 0; c < w; c++)
                    result[ch * h * w + r * w + c] = row[Math.Clamp(cropJ + c, 0, upW - 1)];
            }
        }
        return result;
    }

    /// <summary>
    /// Copies an h x w window starting at (<paramref name="r0"/>, <paramref name="c0"/>) out of a
    /// srcH x srcW raster into a flat array, clamping reads to the raster's edge.
    /// </summary>
    public static float[] CropFlat(float[][] source, int r0, int c0, int h, int w, int srcH, int srcW)
    {
        var output = new float[h * w];
        for (int r = 0; r < h; r++)
        {
            float[] row = source[Math.Clamp(r0 + r, 0, srcH - 1)];
            for (int c = 0; c < w; c++)
                output[r * w + c] = row[Math.Clamp(c0 + c, 0, srcW - 1)];
        }
        return output;
    }
}
