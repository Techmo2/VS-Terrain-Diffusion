using System;
using VSTerrainDiffusion.Core;

namespace VSTerrainDiffusion.Pipeline;

/// <summary>
/// Gives the sea the climate of the land beside it, before the coarse climate is spread to block
/// columns.
///
/// The coarse model was trained on land climate only (WorldClim has none over water), so what it
/// draws over the sea is filler. Measured on seed 777 against land in the same latitude rows, it is
/// 1.5 C warmer at sea level, half as seasonal (BIO4 539 against 855) and 674 mm wetter. Columns
/// are interpolated from the four coarse cells around them and the game's climate map blends sea
/// pixels into the shore, so that filler reached every coast as a mild, wet, frost-free climate:
/// humid subtropical shores at any latitude. Elevation is left alone; only the climate channels of
/// sea cells (mean elevation at or below sea level) are replaced.
///
/// Each sea cell takes a Gaussian-weighted mean of the land cells within <see cref="Radius"/>, with
/// land temperatures carried down to sea level at the reference lapse rate, since the sea is at sea
/// level. A small weight on its own value lets open ocean far from land keep what the model drew,
/// rather than jumping where the nearest land drops out of reach.
/// </summary>
internal static class CoastFill
{
    /// <summary>How far, in coarse cells, a sea cell looks for land. Callers fetch this much margin.</summary>
    public const int Radius = 4;

    private const float Sigma = 1.5f;

    /// <summary>Weight of the sea cell's own value: a land cell three sigmas away.</summary>
    private static readonly float OwnWeight = MathF.Exp(-4.5f);

    private static readonly float[] Kernel = BuildKernel();

    private static float[] BuildKernel()
    {
        int size = 2 * Radius + 1;
        var kernel = new float[size * size];
        for (int dr = -Radius; dr <= Radius; dr++)
            for (int dc = -Radius; dc <= Radius; dc++)
            {
                float d2 = dr * dr + dc * dc;
                kernel[(dr + Radius) * size + dc + Radius] = d2 > Radius * Radius ? 0f : MathF.Exp(-d2 / (2f * Sigma * Sigma));
            }
        return kernel;
    }

    /// <summary>
    /// Fills the sea cells of a coarse window in place. <paramref name="map"/> holds the coarse
    /// channels as h x w planes, elevation (signed square-root metres) first and temperature at
    /// <see cref="CoarseChannel.Temperature"/>; every channel from there on is climate. Cells within
    /// <see cref="Radius"/> of the window's edge are left as they are, so callers fetch a margin
    /// that wide and discard it.
    /// </summary>
    public static void Apply(float[][] map, int h, int w)
    {
        float[] elevation = map[CoarseChannel.Elevation];
        int first = CoarseChannel.Temperature, channels = map.Length;
        float lapsePerMetre = ClimateScale.ReferenceLapseCPerKm / 1000f;
        int size = 2 * Radius + 1;

        // Land values as a sea cell would see them: temperature taken down to sea level.
        var landTemperature = new float[h * w];
        for (int i = 0; i < h * w; i++)
        {
            float e = elevation[i];
            landTemperature[i] = map[CoarseChannel.Temperature][i] + lapsePerMetre * (e > 0f ? e * e : 0f);
        }

        var filled = new float[channels][];
        for (int ch = first; ch < channels; ch++) filled[ch] = (float[])map[ch].Clone();
        var sums = new double[channels];

        for (int r = Radius; r < h - Radius; r++)
        {
            for (int c = Radius; c < w - Radius; c++)
            {
                int index = r * w + c;
                if (elevation[index] > 0f) continue;

                Array.Clear(sums);
                double landWeight = 0.0;
                for (int dr = -Radius; dr <= Radius; dr++)
                {
                    int row = (r + dr) * w;
                    for (int dc = -Radius; dc <= Radius; dc++)
                    {
                        float k = Kernel[(dr + Radius) * size + dc + Radius];
                        int n = row + c + dc;
                        if (k <= 0f || elevation[n] <= 0f) continue;
                        landWeight += k;
                        sums[first] += k * landTemperature[n];
                        for (int ch = first + 1; ch < channels; ch++) sums[ch] += k * map[ch][n];
                    }
                }
                if (landWeight <= 0.0) continue;

                double total = landWeight + OwnWeight;
                for (int ch = first; ch < channels; ch++)
                    filled[ch][index] = (float)((sums[ch] + OwnWeight * map[ch][index]) / total);
            }
        }

        for (int ch = first; ch < channels; ch++) map[ch] = filled[ch];
    }
}
