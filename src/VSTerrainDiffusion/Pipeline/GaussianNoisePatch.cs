using System;

namespace VSTerrainDiffusion.Pipeline;

/// <summary>
/// Deterministic tile-seeded Gaussian noise matching Python world_pipeline.gaussian_noise_patch.
/// </summary>
public static class GaussianNoisePatch
{
    /// <summary>
    /// Returns a flat (channels, h, w) patch of standard-normal noise for the pixel window
    /// starting at (y0, x0). Tiles of (tileH, tileW) are seeded independently so the field is
    /// consistent no matter which window asks for it.
    /// </summary>
    public static float[] Generate(ulong baseSeed, int y0, int x0, int h, int w,
                                   int channels, int tileH, int tileW)
    {
        var output = new float[channels * h * w];

        int ty0 = FloorDiv(y0, tileH);
        int ty1 = FloorDiv(y0 + h - 1, tileH);
        int tx0 = FloorDiv(x0, tileW);
        int tx1 = FloorDiv(x0 + w - 1, tileW);

        int tileLen = channels * tileH * tileW;
        var tileFlat = new float[tileLen];

        for (int ty = ty0; ty <= ty1; ty++)
        {
            int tileY0 = ty * tileH;
            for (int tx = tx0; tx <= tx1; tx++)
            {
                int tileX0 = tx * tileW;

                int oy0 = Math.Max(y0, tileY0);
                int oy1 = Math.Min(y0 + h, tileY0 + tileH);
                int ox0 = Math.Max(x0, tileX0);
                int ox1 = Math.Min(x0 + w, tileX0 + tileW);

                PortableRng.FillStandardNormal(PortableRng.TileSeed(baseSeed, ty, tx), tileFlat, 0, tileLen);

                for (int c = 0; c < channels; c++)
                {
                    int tileChannelBase = c * tileH * tileW;
                    int outChannelBase = c * h * w;
                    for (int py = oy0; py < oy1; py++)
                    {
                        int outRow = outChannelBase + (py - y0) * w;
                        int tileRow = tileChannelBase + (py - tileY0) * tileW;
                        for (int px = ox0; px < ox1; px++)
                        {
                            output[outRow + (px - x0)] = tileFlat[tileRow + (px - tileX0)];
                        }
                    }
                }
            }
        }
        return output;
    }

    private static int FloorDiv(int a, int b)
    {
        int q = a / b;
        if ((a % b != 0) && ((a < 0) != (b < 0))) q--;
        return q;
    }
}
