using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace VSTerrainDiffusion.Pipeline;

/// <summary>
/// A place where something asks for a particular climate or landform - a story structure's forced
/// climate, say - in the coarse model's own pixel coordinates. The coarse model's output climate is
/// overridden: inside <see cref="Radius"/> it is the target, and over the next <see cref="Fade"/> it
/// blends back to what the model drew. The stored coarse map is left as the model drew it; the edit is
/// applied as it is read, per map cell for the base model (so the terrain below half a kilometre
/// follows the new climate) and per block column for the climate the world uses.
///
/// Asking through the model's conditioning instead was tried and does not work at this size: a disc
/// one or two coarse pixels wide is treated as noise (a 30 C request moved the climate 2-3 C), and
/// what response there is comes through elevation - hot requests dug a 2 km crater into a mountain.
/// </summary>
public sealed class CoarseConditioningSite
{
    /// <summary>Centre, in coarse model pixels: I along Z (rows), J along X (columns).</summary>
    public float I, J;

    /// <summary>Full-strength radius and the blend beyond it, in coarse model pixels.</summary>
    public float Radius, Fade;

    /// <summary>Climate targets in real units; null leaves that channel to the model.</summary>
    public float? TemperatureC, Seasonality, PrecipitationMm, PrecipitationCv;

    /// <summary>
    /// Flatten the coarse relief output (mean minus fifth percentile) inside the site, which is what
    /// the base model reads for how rough a place is. Relief is an output, not a conditioning input.
    /// </summary>
    public bool Flatten;

    /// <summary>
    /// How far the elevation the model is asked for is pulled towards sea level inside the site, 0 to 1:
    /// the river basin's rank warp, so the model draws the low, level ground it knows lowlands as.
    /// </summary>
    public float? BasinDepth;

    /// <summary>
    /// The elevation conditioning's noise level (cond_snr) inside the site, lower binding tighter, or
    /// null for the world's. Blended towards the world's across the fade.
    /// </summary>
    public float? ElevationNoise;

    /// <summary>For logs and the tile database fingerprint.</summary>
    public string Label = "";

    /// <summary>Whether the site sets any climate channel.</summary>
    public bool HasClimate => TemperatureC != null || Seasonality != null || PrecipitationMm != null || PrecipitationCv != null;

    /// <summary>How strongly the site applies at a coarse pixel centre: 1 inside, smoothly to 0 across the fade.</summary>
    public float WeightAt(int i, int j) => WeightAtPoint(i + 0.5f, j + 0.5f);

    /// <summary>How strongly the site applies at a point in coarse model pixels.</summary>
    public float WeightAtPoint(float i, float j)
    {
        float di = i - I, dj = j - J;
        float d = MathF.Sqrt(di * di + dj * dj);
        if (d <= Radius) return 1f;
        if (Fade <= 0f || d >= Radius + Fade) return 0f;
        float t = 1f - (d - Radius) / Fade;
        return t * t * (3f - 2f * t);
    }

    /// <summary>
    /// Blends the climate channels of a slice of the stored coarse map towards every site that reaches
    /// it, one value per map cell. The slice is laid out as the map stores it: <paramref name="channels"/>
    /// planes each multiplied by the blend weight, then the weight; cell (r, c) is map cell
    /// (ci0 + r, cj0 + c), which covers <paramref name="poolFactor"/> model pixels a side. For whatever
    /// reads the map at its own resolution - the base model, the climate API, the debug map.
    /// </summary>
    public static void ApplyClimateToSlice(IReadOnlyList<CoarseConditioningSite> sites, float[] data, int channels,
                                           int ci0, int cj0, int h, int w, int poolFactor)
    {
        if (sites == null || sites.Count == 0) return;
        int plane = h * w, n = poolFactor;
        foreach (CoarseConditioningSite site in sites)
        {
            if (!site.HasClimate || !site.Reaches(ci0 * n, cj0 * n, h * n, w * n)) continue;
            for (int r = 0; r < h; r++)
            {
                for (int c = 0; c < w; c++)
                {
                    float weight = site.WeightAtPoint((ci0 + r + 0.5f) * n, (cj0 + c + 0.5f) * n);
                    if (weight <= 0f) continue;
                    int px = r * w + c;
                    float blend = data[channels * plane + px];
                    if (site.TemperatureC is float t) Blend(data, CoarseChannel.Temperature * plane + px, t * blend, weight);
                    if (site.Seasonality is float b) Blend(data, CoarseChannel.TemperatureSeasonality * plane + px, b * blend, weight);
                    if (site.PrecipitationMm is float p) Blend(data, CoarseChannel.Precipitation * plane + px, p * blend, weight);
                    if (site.PrecipitationCv is float v) Blend(data, CoarseChannel.PrecipitationCv * plane + px, v * blend, weight);
                }
            }
        }
    }

    /// <summary>The climate sites that reach a block of coarse model pixels, or null if none do.</summary>
    public static List<CoarseConditioningSite> ClimateSitesNear(IReadOnlyList<CoarseConditioningSite> sites,
                                                                float i0, float j0, float i1, float j1)
    {
        List<CoarseConditioningSite> near = null;
        if (sites == null) return null;
        foreach (CoarseConditioningSite site in sites)
            if (site.HasClimate && site.Reaches((int)MathF.Floor(i0), (int)MathF.Floor(j0),
                    (int)MathF.Ceiling(i1 - i0) + 1, (int)MathF.Ceiling(j1 - j0) + 1))
                (near ??= new List<CoarseConditioningSite>()).Add(site);
        return near;
    }

    /// <summary>
    /// Blends one column's climate towards the sites at its exact position (i, j in coarse model
    /// pixels), so a site's edge is a circle rather than the outline of the map cells it covers.
    /// <paramref name="climate"/> holds temperature, BIO4, precipitation and BIO15 planes
    /// <paramref name="plane"/> apart from <paramref name="index"/>. Temperature moves by the gap between
    /// the target and the coarse temperature there, <paramref name="coarseTemperature"/>, so the column
    /// keeps its own height's lapse: the target is the temperature at the cell's mean elevation.
    /// </summary>
    public static void BlendColumn(List<CoarseConditioningSite> sites, float i, float j, float coarseTemperature,
                                   float[] climate, int plane, int index)
    {
        foreach (CoarseConditioningSite site in sites)
        {
            float weight = site.WeightAtPoint(i, j);
            if (weight <= 0f) continue;
            if (site.TemperatureC is float t)
            {
                float shift = (t - coarseTemperature) * weight;
                climate[index] += shift;
                coarseTemperature += shift;
            }
            if (site.Seasonality is float b) Blend(climate, plane + index, b, weight);
            if (site.PrecipitationMm is float p) Blend(climate, 2 * plane + index, p, weight);
            if (site.PrecipitationCv is float v) Blend(climate, 3 * plane + index, v, weight);
        }
    }

    /// <summary>
    /// Flattens the relief channel of a denormalised coarse output (channel 1 holding elevation minus
    /// fifth percentile, both √m) towards zero inside every flattening site.
    /// </summary>
    public static void ApplyRelief(IReadOnlyList<CoarseConditioningSite> sites, float[] output, int i0, int j0, int s)
    {
        if (sites == null || sites.Count == 0) return;
        int plane = s * s;
        foreach (CoarseConditioningSite site in sites)
        {
            if (!site.Flatten || !site.Reaches(i0, j0, s, s)) continue;
            for (int r = 0; r < s; r++)
            {
                for (int c = 0; c < s; c++)
                {
                    float weight = site.WeightAt(i0 + r, j0 + c);
                    if (weight > 0f) Blend(output, plane + r * s + c, 0f, weight);
                }
            }
        }
    }

    /// <summary>
    /// Basin depth per pixel over the factory's window (x along J, y along I, as the factory is
    /// called), the deepest site winning; null when no site reaches it.
    /// </summary>
    public static float[] BasinDepths(IReadOnlyList<CoarseConditioningSite> sites, int x1, int y1, int x2, int y2)
    {
        if (sites == null) return null;
        int h = y2 - y1, w = x2 - x1;
        float[] depths = null;
        foreach (CoarseConditioningSite site in sites)
        {
            if (site.BasinDepth is not float depth || depth <= 0f || !site.Reaches(y1, x1, h, w)) continue;
            depths ??= new float[h * w];
            for (int r = 0; r < h; r++)
                for (int c = 0; c < w; c++)
                    depths[r * w + c] = Math.Max(depths[r * w + c], depth * site.WeightAt(y1 + r, x1 + c));
        }
        return depths;
    }

    /// <summary>
    /// The elevation conditioning's noise level per pixel of an s x s coarse window at (i0, j0):
    /// the world's, pulled towards each site's own across its weight. Null when no site sets one.
    /// </summary>
    public static float[] ElevationNoiseLevels(IReadOnlyList<CoarseConditioningSite> sites, float worldLevel,
                                               int i0, int j0, int s)
    {
        if (sites == null) return null;
        float[] levels = null;
        foreach (CoarseConditioningSite site in sites)
        {
            if (site.ElevationNoise is not float level || !site.Reaches(i0, j0, s, s)) continue;
            if (levels == null) { levels = new float[s * s]; Array.Fill(levels, worldLevel); }
            for (int r = 0; r < s; r++)
                for (int c = 0; c < s; c++)
                {
                    float weight = site.WeightAt(i0 + r, j0 + c);
                    int k = r * s + c;
                    if (weight > 0f) levels[k] = Math.Min(levels[k], worldLevel + (level - worldLevel) * weight);
                }
        }
        return levels;
    }

    private bool Reaches(int i0, int j0, int h, int w)
    {
        float reach = Radius + Fade + 1f;
        return I + reach >= i0 && I - reach <= i0 + h && J + reach >= j0 && J - reach <= j0 + w;
    }

    private static void Blend(float[] values, int index, float target, float weight) =>
        values[index] += (target - values[index]) * weight;

    /// <summary>A stable description of every site, so stored terrain is invalidated when they change.</summary>
    public static string Fingerprint(IReadOnlyList<CoarseConditioningSite> sites)
    {
        if (sites == null || sites.Count == 0) return "none";
        static string F(float? v) => v?.ToString("0.###", CultureInfo.InvariantCulture) ?? "-";
        return string.Join(";", sites.Select(s =>
            $"{s.Label}@{F(s.I)},{F(s.J)} r{F(s.Radius)}+{F(s.Fade)} t{F(s.TemperatureC)} " +
            $"s{F(s.Seasonality)} p{F(s.PrecipitationMm)} cv{F(s.PrecipitationCv)} {(s.Flatten ? "flat" : "")} " +
            $"b{F(s.BasinDepth)} n{F(s.ElevationNoise)}"));
    }
}
