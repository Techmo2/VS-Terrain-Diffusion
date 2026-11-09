using System;

namespace VSTerrainDiffusion.Core;

/// <summary>
/// Zonal mean climate: the temperature and rainfall a place has for no other reason than how far
/// it sits from the equator.
///
/// Version 1's curves are smooth fits; version 2 (<see cref="CurrentVersion"/>, new worlds) is a table
/// of Earth's land medians by latitude. Either way they are deliberately smooth: everything that makes one place in a belt different from another - the
/// coast, the mountain, the rain shadow, the continental interior - is the model's job, and comes
/// out of it far larger than the error in these.
/// </summary>
public static class LatitudeProfile
{
    /// <summary>Mean annual temperature at the equator, in Celsius.</summary>
    public const float EquatorC = 26f;

    /// <summary>Mean annual temperature at the pole, in Celsius.</summary>
    public const float PoleC = -25f;

    /// <summary>
    /// Version 1: curvature of the temperature profile. The observed one is flat across the tropics and then
    /// nearly straight from the subtropics to the ice, which a plain square is too gentle to
    /// follow; 1.8 splits the difference, landing within about 4 C of the zonal means everywhere
    /// (26 at the equator, 22 at 20 deg, 19 at 30, 14 at 40, 8 at 50, 1 at 60, -6 at 70, -15 at 80).
    /// </summary>
    private const float TemperatureCurve = 1.8f;

    /// <summary>The profile new worlds get; see <see cref="TemperatureC(float, int)"/>.</summary>
    public const int CurrentVersion = 2;

    /// <summary>
    /// Version 2: Earth's land by latitude, from the coarse model's own training data (the coarse
    /// catalogue, 1.68 M land cells, 60 S - 60 N): the median of each 5-degree belt, relief included,
    /// at the belt's middle, then on to the pole by way of the Arctic's land. Version 1's curves were
    /// 4-5 C warmer than this from 35 to 60 degrees and nearly twice as wet, because their figures
    /// were zonal means over land and sea together, where the oceans' storm tracks dominate; a
    /// "temperate" place then sat at storm-track latitudes with ~900 mm where Earth's land at that
    /// temperature has ~500.
    /// </summary>
    private static readonly float[] LandDegrees = { 0f, 2.5f, 7.5f, 12.5f, 17.5f, 22.5f, 27.5f, 32.5f, 37.5f, 42.5f, 47.5f, 52.5f, 57.5f, 65f, 75f, 90f };

    /// <summary>Version 2's median land temperature, °C, at each of <see cref="LandDegrees"/>.</summary>
    private static readonly float[] LandTemperatureC = { 25.6f, 25.6f, 25.6f, 25.7f, 25.8f, 24.0f, 21.1f, 17.2f, 12.2f, 8.5f, 4.9f, 1.4f, -1.0f, -8f, -17f, -25f };

    /// <summary>Version 2's median land rainfall, mm, at each of <see cref="LandDegrees"/>.</summary>
    private static readonly float[] LandPrecipitationMm = { 2013f, 2013f, 1442f, 1033f, 649f, 358f, 252f, 347f, 450f, 457f, 494f, 549f, 515f, 420f, 280f, 180f };

    /// <summary>Mean annual temperature for a place this far from the equator, 0 to 1.</summary>
    public static float TemperatureC(float pole01, int version = CurrentVersion)
    {
        float a = Math.Clamp(pole01, 0f, 1f);
        if (version >= 2) return Table(LandTemperatureC, a * 90f);
        return EquatorC - (EquatorC - PoleC) * (float)Math.Pow(a, TemperatureCurve);
    }

    /// <summary>Interpolates a version 2 table at a latitude.</summary>
    /// <param name="values"><see cref="LandTemperatureC"/> or <see cref="LandPrecipitationMm"/>.</param>
    /// <param name="degrees">Degrees from the equator, 0 to 90.</param>
    private static float Table(float[] values, float degrees)
    {
        for (int i = 1; i < LandDegrees.Length; i++)
        {
            if (degrees > LandDegrees[i]) continue;
            float t = (degrees - LandDegrees[i - 1]) / (LandDegrees[i] - LandDegrees[i - 1]);
            return values[i - 1] + t * (values[i] - values[i - 1]);
        }
        return values[^1];
    }

    /// <summary>
    /// Annual precipitation for a place this far from the equator, in millimetres.
    ///
    /// Version 1: three terms, one per cell of the general circulation. Air rises at the equator and rains
    /// itself out there, which is the tall narrow peak; it comes back down dry at about 25 deg,
    /// which is where every desert on Earth is and which shows up here as the trough between the
    /// two humps; and the polar front throws it up again along the mid-latitude storm track near
    /// 50 deg, which is the second, broader peak. The baseline underneath is the plain fact that
    /// cold air holds less water, so even a wet pole is a dry one by tropical standards.
    /// </summary>
    public static float PrecipitationMm(float pole01, int version = CurrentVersion)
    {
        float degrees = Math.Clamp(pole01, 0f, 1f) * 90f;
        if (version >= 2) return Table(LandPrecipitationMm, degrees);

        float itcz = 1700f * Gaussian(degrees, 0f, 13f);
        float stormTrack = 550f * Gaussian(degrees, 50f, 20f);
        float capacity = 150f + 300f * (float)Math.Sqrt(Math.Max(0.0, Math.Cos(degrees * Math.PI / 180.0)));

        return itcz + stormTrack + capacity;
    }

    /// <summary>exp(-((x - centre) / width)²).</summary>
    private static float Gaussian(float x, float centre, float width)
    {
        float t = (x - centre) / width;
        return (float)Math.Exp(-t * t);
    }
}
